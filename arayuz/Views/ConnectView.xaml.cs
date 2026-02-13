using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using arayuz_deneme_1.Net;

namespace arayuz_deneme_1.Views
{
    public enum ConnectState { Disconnected, Connecting, Connected }

    public sealed class ConnectionStatus
    {
        public ConnectState State { get; set; } = ConnectState.Disconnected;

        public string Host { get; set; } = "";
        public int Port { get; set; }

        public int TeamNo { get; set; }
        public string Username { get; set; } = "";
        public string TokenPreview { get; set; } = "";

        public int RateHz { get; set; }
        public double LastRttMs { get; set; }
        public double SignalQuality { get; set; } // 0..100

        public int Peers { get; set; } = -1;
        public string HealthText { get; set; } = "";

        public string LastInfo { get; set; } = "";
        public string LastError { get; set; } = "";

        // Tooltip’te gösterilecek:
        public string HssText { get; set; } = "-";
        public string QrText { get; set; } = "-";
    }

    public partial class ConnectView : UserControl
    {
        private SihaApiClient? _api;
        private DispatcherTimer? _timer;
        private CancellationTokenSource? _cts;

        // demo telemetry (istersen mavlinkten beslersin)
        private int _teamNo = 1;
        private double _lat = 41.0;
        private double _lon = 29.0;

        // status cache
        private readonly ConnectionStatus _st = new();

        // tick çakışmasını engelle
        private int _tickBusy = 0;

        // QR/HSS polling
        private long _lastQrHssAt = 0;
        private const int QrHssPollMs = 3000;

        public event Action<ConnectionStatus>? Connecting;
        public event Action<ConnectionStatus>? Connected;
        public event Action<ConnectionStatus>? Disconnected;
        public event Action<ConnectionStatus>? StatusChanged;

        // ✅ MAP’e taşımak için eventler
        public event Action<HssCoord[]>? HssUpdated;
        public event Action<QrCoord?>? QrUpdated;

        public ConnectView()
        {
            InitializeComponent();
            SetDisconnected("Hazır.");
        }

        // -------------------- UI helpers --------------------
        private void Log(string s)
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
            LogBox.ScrollToEnd();
        }

        private void UpdateQuickStatus()
        {
            var st = _st.State switch
            {
                ConnectState.Connected => "Connected",
                ConnectState.Connecting => "Connecting",
                _ => "Disconnected"
            };

            var extra = string.IsNullOrWhiteSpace(_st.LastError)
                ? _st.LastInfo
                : ("ERR: " + _st.LastError);

            QuickStatus.Text = $"Durum: {st} | {_st.Host}:{_st.Port} | q={_st.SignalQuality:0}% | {extra}";
        }

        private void EmitAll()
        {
            UpdateQuickStatus();

            StatusChanged?.Invoke(Clone(_st));
            if (_st.State == ConnectState.Connecting) Connecting?.Invoke(Clone(_st));
            else if (_st.State == ConnectState.Connected) Connected?.Invoke(Clone(_st));
            else Disconnected?.Invoke(Clone(_st));
        }

        private static ConnectionStatus Clone(ConnectionStatus s) => new()
        {
            State = s.State,
            Host = s.Host,
            Port = s.Port,
            TeamNo = s.TeamNo,
            Username = s.Username,
            TokenPreview = s.TokenPreview,
            RateHz = s.RateHz,
            LastRttMs = s.LastRttMs,
            SignalQuality = s.SignalQuality,
            Peers = s.Peers,
            HealthText = s.HealthText,
            LastInfo = s.LastInfo,
            LastError = s.LastError,
            HssText = s.HssText,
            QrText = s.QrText
        };

        private void SetConnecting(string info)
        {
            _st.State = ConnectState.Connecting;
            _st.LastInfo = info;
            _st.LastError = "";
            EmitAll();
        }

        private void SetConnected(string info)
        {
            _st.State = ConnectState.Connected;
            _st.LastInfo = info;
            _st.LastError = "";
            if (_st.SignalQuality <= 0) _st.SignalQuality = 100;
            EmitAll();
        }

        private void SetDisconnected(string errOrInfo, bool isError = false)
        {
            _st.State = ConnectState.Disconnected;
            _st.LastRttMs = 0;
            _st.SignalQuality = 0;
            _st.Peers = -1;

            if (isError)
            {
                _st.LastError = errOrInfo;
                _st.LastInfo = "Bağlantı yok.";
            }
            else
            {
                _st.LastError = "";
                _st.LastInfo = errOrInfo;
            }

            _st.HssText = _st.HssText ?? "-";
            _st.QrText = _st.QrText ?? "-";

            EmitAll();
        }

        private static double Clamp(double v, double a, double b) => v < a ? a : (v > b ? b : v);

        private static double RttToQuality(double rttMs)
        {
            if (rttMs <= 0) return 100;
            double q = 100.0 * Math.Exp(-rttMs / 650.0);
            return Clamp(q, 0, 100);
        }

        // -------------------- Button handlers --------------------
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopSending(); // önceki varsa temizle

                string host = ServerHostBox.Text.Trim();
                int port = int.Parse(ServerPortBox.Text.Trim(), CultureInfo.InvariantCulture);

                string kadi = UsernameBox.Text.Trim();
                string sifre = PasswordBox.Password;

                _teamNo = int.Parse(TeamNoBox.Text.Trim(), CultureInfo.InvariantCulture);

                int hz = int.Parse(RateHzBox.Text.Trim(), CultureInfo.InvariantCulture);
                if (hz <= 0) hz = 1;
                if (hz > 2) hz = 2; // server 2Hz üstüne izin vermiyor
                int intervalMs = (int)Math.Round(1000.0 / hz);

                _st.Host = host;
                _st.Port = port;
                _st.TeamNo = _teamNo;
                _st.Username = kadi;
                _st.RateHz = hz;
                _st.TokenPreview = "";
                _st.Peers = -1;
                _st.HealthText = "";
                _st.HssText = "-";
                _st.QrText = "-";

                _lastQrHssAt = 0;

                SetConnecting("Health check…");
                _api = new SihaApiClient(host, port);

                Log($"Health check: {_api.BaseUrl}");
                var t0 = DateTime.UtcNow;
                var health = await _api.HealthAsync();
                var rtt = (DateTime.UtcNow - t0).TotalMilliseconds;

                _st.HealthText = health?.ToString() ?? "OK";
                _st.LastRttMs = rtt;
                _st.SignalQuality = RttToQuality(rtt);

                Log($"Health OK: {health} ({rtt:0}ms)");

                SetConnecting("Login…");
                Log("Login...");

                t0 = DateTime.UtcNow;
                var login = await _api.LoginAsync(kadi, sifre);
                rtt = (DateTime.UtcNow - t0).TotalMilliseconds;

                _st.LastRttMs = rtt;
                _st.SignalQuality = RttToQuality(rtt);
                _st.TeamNo = login.TeamNo;

                var tok = login.Token ?? "";
                _st.TokenPreview = tok.Length <= 8 ? tok : (tok[..8] + "…");

                Log($"Login OK -> team={login.TeamNo} token={_st.TokenPreview} ({rtt:0}ms)");

                if (login.TeamNo != _teamNo)
                    Log($"UYARI: Login team({login.TeamNo}) != Form team({_teamNo})");

                // ✅ login sonrası ilk QR/HSS çek
                await PollQrHssOnceSafe();

                SetConnected("Bağlandı.");

                if (AutoStartChk.IsChecked == true)
                    StartSending(intervalMs);
                else
                    Log("Bağlandı. Otomatik gönder kapalı.");
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(ex.Message, "Connect Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetDisconnected(ex.Message, isError: true);
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            StopSending();

            _api?.Dispose();
            _api = null;

            SetDisconnected("Gönderim durduruldu.");
        }

        // -------------------- Sender loop --------------------
        private void StartSending(int intervalMs)
        {
            StopSending();

            if (_api == null || string.IsNullOrWhiteSpace(_api.Token))
            {
                Log("Token yok, önce login ol.");
                SetDisconnected("Token yok.", isError: true);
                return;
            }

            _cts = new CancellationTokenSource();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };

            _timer.Tick += async (_, __) =>
            {
                if (_api == null || _cts == null) return;

                // tick overlap engelle
                if (Interlocked.Exchange(ref _tickBusy, 1) == 1)
                    return;

                try
                {
                    // demo telemetry
                    var payload = TelemetryFactory.MakeDemoTelemetry(_teamNo, _lat, _lon, locked: false);

                    var t0 = DateTime.UtcNow;
                    var res = await _api.SendTelemetryAsync(payload, _cts.Token);
                    var rtt = (DateTime.UtcNow - t0).TotalMilliseconds;

                    _st.LastRttMs = rtt;
                    _st.SignalQuality = RttToQuality(rtt);

                    _st.Peers = res?.konumBilgileri?.Length ?? 0;

                    // 3 saniyede bir QR/HSS çek
                    var now = Environment.TickCount64;
                    if (now - _lastQrHssAt >= QrHssPollMs)
                    {
                        _lastQrHssAt = now;
                        await PollQrHssOnceSafe();
                    }

                    _st.LastInfo = $"Telemetri OK | peers={_st.Peers}";
                    _st.LastError = "";
                    _st.State = ConnectState.Connected;

                    StatusChanged?.Invoke(Clone(_st));
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    // First-chance exception görüyorsun; burada log’u büyütelim:
                    Log("TEL FAIL: " + ex.Message);
                    System.Diagnostics.Debug.WriteLine("TEL FAIL FULL: " + ex);

                    _st.LastError = ex.Message;
                    _st.LastInfo = "Telemetri FAIL";
                    _st.SignalQuality = Clamp(_st.SignalQuality * 0.6, 0, 100);
                    _st.State = (_st.SignalQuality <= 5) ? ConnectState.Disconnected : ConnectState.Connecting;

                    StatusChanged?.Invoke(Clone(_st));

                    if (_st.State == ConnectState.Disconnected)
                        Disconnected?.Invoke(Clone(_st));
                }
                finally
                {
                    Interlocked.Exchange(ref _tickBusy, 0);
                }
            };

            _timer.Start();
            Log($"Gönderim başladı. interval={intervalMs}ms");
        }

        private void StopSending()
        {
            try
            {
                _timer?.Stop();
                _timer = null;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                Interlocked.Exchange(ref _tickBusy, 0);

                Log("Gönderim durduruldu.");
            }
            catch { }
        }

        // -------------------- QR/HSS polling --------------------
        private async Task PollQrHssOnceSafe()
        {
            if (_api == null) return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var qr = await _api.GetQrAsync(cts.Token);
                var hss = await _api.GetHssAsync(cts.Token);

                // ✅ DEBUG
                System.Diagnostics.Debug.WriteLine(
                    $"HSS count = {hss?.hss_koordinat_bilgileri?.Length ?? 0}, " +
                    $"first=({hss?.hss_koordinat_bilgileri?[0].hssEnlem},{hss?.hss_koordinat_bilgileri?[0].hssBoylam}) " +
                    $"r={hss?.hss_koordinat_bilgileri?[0].hssYaricap}"
                );

                if (qr != null)
                    _st.QrText = $"{qr.qrEnlem:0.00000000}, {qr.qrBoylam:0.00000000}";
                else
                    _st.QrText = "-";

                if (hss?.hss_koordinat_bilgileri != null && hss.hss_koordinat_bilgileri.Length > 0)
                    _st.HssText = BuildHssSummary(hss.hss_koordinat_bilgileri);
                else
                    _st.HssText = "-";

                Log($"QR/HSS OK | QR={_st.QrText} | HSS={(_st.HssText.Length > 60 ? _st.HssText[..60] + "…" : _st.HssText)}");

                // ✅ MAP’e taşı
                QrUpdated?.Invoke(qr);
                if (hss?.hss_koordinat_bilgileri != null)
                    HssUpdated?.Invoke(hss.hss_koordinat_bilgileri);

                StatusChanged?.Invoke(Clone(_st));
            }
            catch (Exception ex)
            {
                // QR/HSS gelmedi diye bağlantıyı düşürme.
                Log("QR/HSS FAIL: " + ex.Message);
                System.Diagnostics.Debug.WriteLine("QR/HSS FAIL FULL: " + ex);
            }
        }

        private static string BuildHssSummary(HssCoord[] list)
        {
            var sb = new StringBuilder();
            sb.Append($"{list.Length} adet");

            foreach (var h in list)
            {
                sb.Append($" | [{h.id}] {h.hssEnlem:0.00000000},{h.hssBoylam:0.00000000} r={h.hssYaricap:0}m");
                if (sb.Length > 260)
                {
                    sb.Append(" ...");
                    break;
                }
            }
            return sb.ToString();
        }

        // --------------------
        // Demo TelemetryFactory
        // --------------------
        internal static class TelemetryFactory
        {
            public static TelemetryPayload MakeDemoTelemetry(int teamNo, double lat, double lon, bool locked)
            {
                var gps = new GpsSaati
                {
                    saat = DateTime.UtcNow.Hour,
                    dakika = DateTime.UtcNow.Minute,
                    saniye = DateTime.UtcNow.Second,
                    milisaniye = DateTime.UtcNow.Millisecond
                };

                return new TelemetryPayload
                {
                    takim_numarasi = teamNo,
                    iha_enlem = lat,
                    iha_boylam = lon,
                    iha_irtifa = 50,
                    iha_dikilme = 0,
                    iha_yonelme = 0,
                    iha_yatis = 0,
                    iha_hiz = 20,
                    iha_batarya = 80,
                    iha_otonom = 1,
                    iha_kilitlenme = locked ? 1 : 0,
                    hedef_merkez_X = locked ? 320 : 0,
                    hedef_merkez_Y = locked ? 240 : 0,
                    hedef_genislik = locked ? 40 : 0,
                    hedef_yukseklik = locked ? 60 : 0,
                    gps_saati = gps
                };
            }
        }
    }
}
