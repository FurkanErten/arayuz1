using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

// ✅ EKLENDİ
using arayuz_deneme_1.Views;

namespace arayuz_deneme_1
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private data? _data;
        private PlanWaypointPanel? _plan;
        private ConfigPanel? _config;
        private SetupPanel? _setup;
        private WelcomeLogin? _login;

        private ConnectView? _connectView;
        private bool _connectHooked;

        private MyMavlinkClient? _mav;

        private bool _initialized;

        private enum PanelKind { Data, Plan, Config, Setup }
        private PanelKind _currentPanel = PanelKind.Data;

        private SerialPort? _serial;

        // --- TCP yerine UDP ---
        private UdpClient? _udp;
        private IPEndPoint? _udpRemote;

        private CancellationTokenSource? _cts;
        private readonly MavlinkReader _mavReader = new();

        private TelemetryDto _last = new()
        {
            pitchDeg = double.NaN,
            rollDeg = double.NaN,
            headingDeg = double.NaN,
            altitude = double.NaN,
            lat = double.NaN,
            lon = double.NaN,
            airspeed = double.NaN,
            groundspeed = double.NaN,
            battVolt = double.NaN
        };

        private byte _targetSys, _targetComp;
        private volatile bool _haveTarget;
        private byte _seqTx;

        private readonly object _paramLock = new();
        private bool _paramDownloading;
        private int _paramCount = -1;
        private readonly HashSet<int> _gotIdx = new();
        private DateTime _lastParamAt = DateTime.MinValue;

        private const byte MAV_V2 = 0xFD;
        private const byte CRC_EXTRA_PARAM_REQUEST_LIST = 159; // msg 21

        // ===================== CONNECT MENU STATE (BIND) =====================
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private string _connectHeader = "Connect ● 0%";
        public string ConnectHeader
        {
            get => _connectHeader;
            set { _connectHeader = value; OnPropertyChanged(); }
        }

        private Brush _connectBrush = Brushes.IndianRed;
        public Brush ConnectBrush
        {
            get => _connectBrush;
            set { _connectBrush = value; OnPropertyChanged(); }
        }

        public string ConnectTipTitle { get; set; } = "Bağlı Değil";
        public string ConnectTipSubtitle { get; set; } = "-";
        public string ConnectTipServer { get; set; } = "-";
        public string ConnectTipTeam { get; set; } = "-";
        public string ConnectTipUser { get; set; } = "-";
        public string ConnectTipToken { get; set; } = "-";
        public string ConnectTipRate { get; set; } = "-";
        public string ConnectTipRtt { get; set; } = "-";
        public string ConnectTipPeers { get; set; } = "-";
        public string ConnectTipHealth { get; set; } = "-";
        public string ConnectTipLast { get; set; } = "-";
        public string ConnectTipError { get; set; } = "";

        // ✅ EK: HSS / QR
        public string ConnectTipHss { get; set; } = "-";
        public string ConnectTipQr { get; set; } = "-";

        private void RefreshConnectTooltip()
        {
            OnPropertyChanged(nameof(ConnectTipTitle));
            OnPropertyChanged(nameof(ConnectTipSubtitle));
            OnPropertyChanged(nameof(ConnectTipServer));
            OnPropertyChanged(nameof(ConnectTipTeam));
            OnPropertyChanged(nameof(ConnectTipUser));
            OnPropertyChanged(nameof(ConnectTipToken));
            OnPropertyChanged(nameof(ConnectTipRate));
            OnPropertyChanged(nameof(ConnectTipRtt));
            OnPropertyChanged(nameof(ConnectTipPeers));
            OnPropertyChanged(nameof(ConnectTipHealth));
            OnPropertyChanged(nameof(ConnectTipLast));
            OnPropertyChanged(nameof(ConnectTipError));

            // ✅ EK
            OnPropertyChanged(nameof(ConnectTipHss));
            OnPropertyChanged(nameof(ConnectTipQr));
        }

        private sealed class ComItem
        {
            public string Port { get; init; } = "";
            public string Label { get; init; } = "";
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            SizeChanged += (_, __) => AlignActiveBar(false);
            StateChanged += async (_, __) =>
            {
                if (WindowState is WindowState.Normal or WindowState.Maximized)
                    await AnimateRestoreAsync();
            };

            ParamFeed.OnRequestAll += OnRequestAllParams;
        }

        // ✅ Üst menüdeki Connect’e basınca Connect sayfasını aç
        private void OpenConnect_Click(object sender, RoutedEventArgs e)
        {
            if (MainHost == null || MainHost.Visibility != Visibility.Visible)
            {
                MessageBox.Show("Önce giriş yapmalısın.");
                return;
            }

            if (_connectView == null)
            {
                _connectView = new ConnectView();
            }

            HookConnectIfNeeded();
            CONTENT_HOST.Content = _connectView;
        }

        private void HookConnectIfNeeded()
        {
            if (_connectView == null) return;
            if (_connectHooked) return;
            _connectHooked = true;

            _connectView.Connecting += st =>
            {
                Dispatcher.Invoke(() =>
                {
                    ConnectBrush = Brushes.Orange;
                    ConnectHeader = "Connect ● …";
                    ApplyConnectStatus(st);
                });
            };

            _connectView.Connected += st =>
            {
                Dispatcher.Invoke(() =>
                {
                    ConnectBrush = Brushes.LimeGreen;
                    ConnectHeader = $"Connect ● {st.SignalQuality:0}%";
                    ApplyConnectStatus(st);
                });
            };

            _connectView.Disconnected += st =>
            {
                Dispatcher.Invoke(() =>
                {
                    ConnectBrush = Brushes.IndianRed;
                    ConnectHeader = "Connect ● 0%";
                    ApplyConnectStatus(st);
                });
            };

            _connectView.StatusChanged += st =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (st.State == ConnectState.Connected)
                    {
                        ConnectBrush = Brushes.LimeGreen;
                        ConnectHeader = $"Connect ● {st.SignalQuality:0}%";
                    }
                    else if (st.State == ConnectState.Connecting)
                    {
                        ConnectBrush = Brushes.Orange;
                        ConnectHeader = "Connect ● …";
                    }
                    else
                    {
                        ConnectBrush = Brushes.IndianRed;
                        ConnectHeader = "Connect ● 0%";
                    }

                    ApplyConnectStatus(st);
                });
            };
        }

        private void ApplyConnectStatus(ConnectionStatus st)
        {
            ConnectTipTitle = st.State switch
            {
                ConnectState.Connecting => "Bağlanıyor…",
                ConnectState.Connected => "Bağlı",
                _ => "Bağlı Değil"
            };

            ConnectTipSubtitle = $"Durum: {st.State} | Güncelleme: {DateTime.Now:HH:mm:ss}";
            ConnectTipServer = $"{st.Host}:{st.Port}";
            ConnectTipTeam = st.TeamNo > 0 ? st.TeamNo.ToString() : "-";
            ConnectTipUser = string.IsNullOrWhiteSpace(st.Username) ? "-" : st.Username;
            ConnectTipToken = string.IsNullOrWhiteSpace(st.TokenPreview) ? "-" : st.TokenPreview;
            ConnectTipRate = st.RateHz > 0 ? $"{st.RateHz} Hz" : "-";
            ConnectTipRtt = $"{st.LastRttMs:0} ms | {st.SignalQuality:0}%";
            ConnectTipPeers = st.Peers >= 0 ? st.Peers.ToString() : "-";
            ConnectTipHealth = string.IsNullOrWhiteSpace(st.HealthText) ? "-" : st.HealthText;
            ConnectTipLast = string.IsNullOrWhiteSpace(st.LastInfo) ? "-" : st.LastInfo;
            ConnectTipError = string.IsNullOrWhiteSpace(st.LastError) ? "" : ("Hata: " + st.LastError);

            // ✅ HSS / QR
            ConnectTipHss = string.IsNullOrWhiteSpace(st.HssText) ? "-" : st.HssText;
            ConnectTipQr = string.IsNullOrWhiteSpace(st.QrText) ? "-" : st.QrText;

            RefreshConnectTooltip();
        }
        // === STATUS HELPER ===
        private void SetStatus(string text, Brush? brush = null)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (StatusText == null) return;
                    StatusText.Text = text;
                    if (brush != null)
                        StatusText.Foreground = brush;
                });
            }
            catch { }
        }

        private void MainWindow_Loaded(object? sender, EventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            _login = new WelcomeLogin();
            _login.LoginSucceeded += OnLoginSucceeded;
            OverlayHost.Content = _login;

            OverlayLayer.Visibility = Visibility.Visible;
            OverlayLayer.Opacity = 0;
            ((Storyboard)FindResource("LoginFadeIn")).Begin();

            InitConnectionCard();
            RefreshComPorts();
            SetStatus("Durum: Beklemede", Brushes.Gray);

            // default connect görünümü
            ConnectBrush = Brushes.IndianRed;
            ConnectHeader = "Connect ● 0%";
            ConnectTipTitle = "Bağlı Değil";
            ConnectTipSubtitle = "Henüz bağlantı yok.";


            RefreshConnectTooltip();

            TelemetryHub.Instance.OnPacket += dto =>
            {
                Dispatcher.Invoke(() =>
                {
                    SetStatus($"TEL: Lat={dto.lat:0.0000} Lon={dto.lon:0.0000} Alt={dto.altitude:0.0}", Brushes.Cyan);

                    if (_data != null)
                    {
                        var payload = new Net.TelemetryPayload
                        {
                            takim_numarasi = 1,
                            iha_enlem = dto.lat,
                            iha_boylam = dto.lon,
                            iha_irtifa = dto.altitude,
                            iha_yonelme = dto.headingDeg
                        };

                        _data.ApplyTelemetryToMap(payload, null);
                    }
                });
            };

        }

        private void OnLoginSucceeded(object? sender, string username)
        {
            _data = new data();
            _plan = new PlanWaypointPanel();
            _config = new ConfigPanel();
            _setup = new SetupPanel();
            _setup.Applied += (_, s) => SetupPanel.ApplyToApplication(s);
            _setup.Reset += (_, __) => SetupPanel.ApplyToApplication(ThemeSettings.Defaults());

            _connectView = new ConnectView();
            HookConnectIfNeeded();

            TryBindCommandsToData();

            MainHost.Visibility = Visibility.Visible;
            MainHost.Opacity = 0;

            if (MainHost.RenderTransform is TransformGroup tg &&
                tg.Children[0] is ScaleTransform sc)
            {
                sc.ScaleX = 0.97;
                sc.ScaleY = 0.97;
            }

            ShowPanel(_data, PanelKind.Data);
            Dispatcher.BeginInvoke(new Action(() => AlignActiveBar(false)));

            Title = $"LAGARİ Ground Station — Hoş geldin, {username}";

            var sb = (Storyboard)FindResource("LoginTransition");
            sb.Completed += (_, __) =>
            {
                OverlayHost.Content = null;
                OverlayLayer.Visibility = Visibility.Collapsed;
                OverlayLayer.Opacity = 1;
            };
            Dispatcher.InvokeAsync(() =>
            {
                ShowPanel(_data, PanelKind.Data);
            });

            sb.Begin();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            try { ParamFeed.OnRequestAll -= OnRequestAllParams; } catch { }
            try { TelemetryHub.Instance.Detach(); } catch { }
            try { TelemetryHub.Instance.Stop(); } catch { }

            try { _cts?.Cancel(); } catch { }

            try
            {
                if (_serial is { IsOpen: true })
                {
                    _serial.Close();
                    _serial.Dispose();
                }
            }
            catch { }

            try
            {
                if (_udp != null)
                {
                    _udp.Close();
                    _udp = null;
                }
            }
            catch { }

            try { (_data as IDisposable)?.Dispose(); } catch { }
            try { (_plan as IDisposable)?.Dispose(); } catch { }
            try { (_config as IDisposable)?.Dispose(); } catch { }
            try { (_setup as IDisposable)?.Dispose(); } catch { }
        }

        private void ShowPanel(UserControl? panel, PanelKind kind)
        {
            if (panel == null || CONTENT_HOST == null) return;
            if (ReferenceEquals(CONTENT_HOST.Content, panel) && _currentPanel == kind) return;

            CONTENT_HOST.Content = panel;
            _currentPanel = kind;
        }

        private void DATA_BUTTON_click(object s, RoutedEventArgs e)
        {
            ShowPanel(_data, PanelKind.Data);
            AlignActiveBar(true, s as Button);
        }

        private void PLAN_BUTTON_click(object s, RoutedEventArgs e)
        {
            ShowPanel(_plan, PanelKind.Plan);
            AlignActiveBar(true, s as Button);
        }

        private void CONFIG_BUTTON_Click(object s, RoutedEventArgs e)
        {
            ShowPanel(_config, PanelKind.Config);
            AlignActiveBar(true, s as Button);
        }

        private void SETUP_BUTTON_Click(object s, RoutedEventArgs e)
        {
            ShowPanel(_setup, PanelKind.Setup);
            AlignActiveBar(true, s as Button);
        }

        private void AlignActiveBar(bool animate, Button? explicitTargetBtn = null, int ms = 220)
        {
            if (ActiveBar == null) return;

            var targetBtn = explicitTargetBtn ?? GetButtonForCurrentPanel();
            if (targetBtn == null) return;

            var (left, width) = ComputeBarGeometry(targetBtn);

            double barWidth = ActiveBar.Width > 0 ? ActiveBar.Width : width;
            double leftCentered = left + (width - barWidth) / 2.0;

            var m = ActiveBar.Margin;

            if (!animate)
            {
                ActiveBar.BeginAnimation(Border.MarginProperty, null);
                ActiveBar.Margin = new Thickness(leftCentered, m.Top, m.Right, m.Bottom);
                return;
            }

            var anim = new ThicknessAnimation
            {
                From = new Thickness(ActiveBar.Margin.Left, m.Top, m.Right, m.Bottom),
                To = new Thickness(leftCentered, m.Top, m.Right, m.Bottom),
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            ActiveBar.BeginAnimation(Border.MarginProperty, anim);
        }

        private Button? GetButtonForCurrentPanel() => _currentPanel switch
        {
            PanelKind.Data => DATA_BUTTON,
            PanelKind.Plan => PLAN_BUTTON,
            PanelKind.Config => CONFIG_BUTTON,
            PanelKind.Setup => SETUP_BUTTON,
            _ => null
        };

        private (double left, double width) ComputeBarGeometry(Button targetBtn)
        {
            if (ActiveBar?.Parent is not FrameworkElement barRow)
                return (0, targetBtn.ActualWidth > 0 ? targetBtn.ActualWidth : 190);

            Point btnToRoot = targetBtn.TransformToAncestor(this).Transform(new Point(0, 0));
            Point barToRoot = barRow.TransformToAncestor(this).Transform(new Point(0, 0));

            double left = btnToRoot.X - barToRoot.X;
            double width = targetBtn.ActualWidth > 0 ? targetBtn.ActualWidth : 190;

            return (left, width);
        }

        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            if (e.ClickCount == 2)
            {
                WindowState = (WindowState == WindowState.Maximized)
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }

        private async Task AnimateRestoreAsync() => await Task.CompletedTask;
        private async Task AnimateMinimizeToTaskbarAsync() => await Task.CompletedTask;
        private void ResetMainHostTransform() { }

        private void InitConnectionCard()
        {
            try
            {
                if (SourceCombo != null)
                    SourceCombo.SelectionChanged += SourceCombo_SelectionChanged;

                if (BaudRateCombo != null)
                {
                    BaudRateCombo.ItemsSource = new[] { "57600", "115200", "230400", "460800", "921600" };
                    BaudRateCombo.SelectedItem = "115200";
                }

                if (COMPanel != null) COMPanel.Visibility = Visibility.Visible;
                if (ServerPanel != null) ServerPanel.Visibility = Visibility.Collapsed;
                if (SimPanel != null) SimPanel.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void RefreshComPorts()
        {
            try
            {
                var list = new List<ComItem>();

                using var searcher =
                    new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var name = mo["Name"]?.ToString() ?? "";
                    int i1 = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
                    int i2 = name.LastIndexOf(')');
                    if (i1 >= 0 && i2 > i1)
                    {
                        var port = name.Substring(i1 + 1, i2 - (i1 + 1));
                        list.Add(new ComItem { Port = port, Label = $"{port} — {name[..i1].Trim()}" });
                    }
                }

                var onlyPorts = SerialPort.GetPortNames()
                    .OrderBy(p => (p.StartsWith("COM") && int.TryParse(p[3..], out var n)) ? n : int.MaxValue)
                    .ToArray();

                foreach (var p in onlyPorts)
                    if (!list.Any(x => x.Port.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new ComItem { Port = p, Label = p });

                ComPortCombo.ItemsSource = list;
                ComPortCombo.DisplayMemberPath = "Label";
                ComPortCombo.SelectedValuePath = "Port";

                if (list.Count > 0 && ComPortCombo.SelectedValue is null)
                    ComPortCombo.SelectedIndex = 0;
            }
            catch
            {
                var fb = SerialPort.GetPortNames().OrderBy(x => x)
                    .Select(p => new ComItem { Port = p, Label = p }).ToList();

                ComPortCombo.ItemsSource = fb;
                ComPortCombo.DisplayMemberPath = "Label";
                ComPortCombo.SelectedValuePath = "Port";

                if (fb.Count > 0)
                    ComPortCombo.SelectedIndex = 0;
            }
        }

        private void SourceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            try
            {
                var src = (SourceCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();

                bool isCom = string.Equals(src, "COM", StringComparison.OrdinalIgnoreCase);
                bool isServer = string.Equals(src, "Server", StringComparison.OrdinalIgnoreCase);
                bool isSim = string.Equals(src, "Simülasyon", StringComparison.OrdinalIgnoreCase);

                if (COMPanel != null)
                    COMPanel.Visibility = isCom ? Visibility.Visible : Visibility.Collapsed;

                if (ServerPanel != null)
                    ServerPanel.Visibility = isServer ? Visibility.Visible : Visibility.Collapsed;

                if (SimPanel != null)
                    SimPanel.Visibility = isSim ? Visibility.Visible : Visibility.Collapsed;

                SetStatus($"Durum: Kaynak = {src}", Brushes.Gray);
            }
            catch { }
        }

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            var src = (SourceCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            SetStatus($"Durum: {src} için bağlantı deneniyor...", Brushes.LightGray);

            if (string.Equals(src, "COM", StringComparison.OrdinalIgnoreCase))
            {
                ConnectSerial();
            }
            else if (string.Equals(src, "Server", StringComparison.OrdinalIgnoreCase))
            {
                ConnectMavlinkUdp(fromSimPanel: false);
            }
            else if (string.Equals(src, "Simülasyon", StringComparison.OrdinalIgnoreCase))
            {
                ConnectMavlinkUdp(fromSimPanel: true);
            }
        }

        // === COM (MAVLink / Serial) ===
        private void ConnectSerial()
        {
            if (_serial is { IsOpen: true })
            {
                _cts?.Cancel();
                try { _serial.Close(); _serial.Dispose(); } catch { }
                _serial = null;
                _mav = null;
                if (ConnectBtn != null) ConnectBtn.Content = "Connect";
                SetStatus("Durum: SERIAL bağlantısı kapatıldı.", Brushes.Gray);
                return;
            }

            var com = (ComPortCombo?.SelectedValue ?? "").ToString();
            var baudStr = (BaudRateCombo?.SelectedItem ?? "").ToString();

            if (string.IsNullOrWhiteSpace(com) || !int.TryParse(baudStr, out int baud))
            {
                MessageBox.Show("COM ve Rate seç.");
                SetStatus("Durum: COM / baud seçilmedi.", Brushes.OrangeRed);
                return;
            }

            try
            {
                SetStatus($"Durum: SERIAL bağlanıyor → {com} @ {baud}", Brushes.LightGray);

                _serial = new SerialPort(com, baud, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    Handshake = Handshake.None,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serial.Open();

                _mav = new MyMavlinkClient(
                    writer: bytes =>
                    {
                        if (_serial is { IsOpen: true })
                            _serial.Write(bytes, 0, bytes.Length);
                    },
                    isConnected: () => _serial?.IsOpen == true
                );

                TryBindCommandsToData();

                _cts = new CancellationTokenSource();
                if (ConnectBtn != null) ConnectBtn.Content = "Disconnect";

                lock (_paramLock)
                {
                    _haveTarget = false;
                    _targetSys = _targetComp = 0;
                    _paramDownloading = false;
                    _paramCount = -1;
                    _gotIdx.Clear();
                    _lastParamAt = DateTime.MinValue;
                    _seqTx = 0;
                }

                Task.Run(() => SerialReadLoop(_cts.Token));
                SetStatus("Durum: SERIAL BAĞLANDI, telemetri bekleniyor...", Brushes.LimeGreen);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Bağlantı hatası: " + ex.Message);
                try { _serial?.Dispose(); } catch { }
                _serial = null;
                _mav = null;
                SetStatus("Durum: SERIAL hata → " + ex.Message, Brushes.OrangeRed);
            }
        }

        // === MAVLINK UDP (Server + Simülasyon) ===
        private void ConnectMavlinkUdp(bool fromSimPanel)
        {
            if (_udp != null)
            {
                _cts?.Cancel();
                try { _udp.Close(); } catch { }

                _udp = null;
                _udpRemote = null;
                _mav = null;

                if (ConnectBtn != null) ConnectBtn.Content = "Connect";
                SetStatus("Durum: UDP MAVLink bağlantısı kapatıldı.", Brushes.Gray);
                return;
            }

            string? host;
            string? portText;

            if (fromSimPanel)
            {
                host = SimHostBox?.Text?.Trim();
                portText = SimPortBox?.Text?.Trim();
            }
            else
            {
                host = ServerHostBox?.Text?.Trim();
                portText = ServerPortBox?.Text?.Trim();
            }

            if (string.IsNullOrWhiteSpace(portText) || !int.TryParse(portText, out int port) || port <= 0)
            {
                MessageBox.Show("Geçerli port giriniz.");
                SetStatus("Durum: UDP port hatalı.", Brushes.OrangeRed);
                return;
            }

            try
            {
                SetStatus($"Durum: UDP MAVLink dinleniyor → port {port}", Brushes.LightGray);

                _udp = new UdpClient(port);
                _udp.Client.ReceiveTimeout = 2000;

                _mav = new MyMavlinkClient(
                    writer: bytes =>
                    {
                        try
                        {
                            if (_udp != null && _udpRemote != null)
                                _udp.Send(bytes, bytes.Length, _udpRemote);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("UDP send err: " + ex.Message);
                        }
                    },
                    isConnected: () => _udp != null
                );

                TryBindCommandsToData();

                _cts = new CancellationTokenSource();
                if (ConnectBtn != null) ConnectBtn.Content = "Disconnect";

                lock (_paramLock)
                {
                    _haveTarget = false;
                    _targetSys = _targetComp = 0;
                    _paramDownloading = false;
                    _paramCount = -1;
                    _gotIdx.Clear();
                    _lastParamAt = DateTime.MinValue;
                    _seqTx = 0;
                }

                Task.Run(() => UdpReadLoop(_cts.Token));
                SetStatus("Durum: UDP MAVLink BAŞLADI, telemetri bekleniyor...", Brushes.LimeGreen);
            }
            catch (Exception ex)
            {
                try { _udp?.Close(); } catch { }
                _udp = null;
                _udpRemote = null;
                _mav = null;

                MessageBox.Show("MAVLink UDP başlatma hatası: " + ex.Message);
                SetStatus("Durum: UDP MAVLink hata → " + ex.Message, Brushes.OrangeRed);
            }
        }

        private void SerialReadLoop(CancellationToken ct)
        {
            var buf = new byte[4096];
            bool firstPacket = true;

            while (!ct.IsCancellationRequested && _serial != null && _serial.IsOpen)
            {
                try
                {
                    int n = _serial.Read(buf, 0, buf.Length);
                    if (n <= 0) continue;

                    if (firstPacket)
                    {
                        firstPacket = false;
                        SetStatus("Durum: SERIAL → Telemetri alınıyor.", Brushes.LimeGreen);
                    }

                    HandleMavlinkBytes(buf, n);
                }
                catch (TimeoutException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Serial err: " + ex.Message);
                    SetStatus("Durum: SERIAL okuma hatası → " + ex.Message, Brushes.OrangeRed);
                    Thread.Sleep(100);
                }
            }

            SetStatus("Durum: SERIAL bağlantı döngüsü bitti (kopmuş olabilir).", Brushes.Gray);
        }

        private void UdpReadLoop(CancellationToken ct)
        {
            bool firstPacket = true;

            while (!ct.IsCancellationRequested && _udp != null)
            {
                try
                {
                    IPEndPoint? remote = new IPEndPoint(IPAddress.Any, 0);
                    var buf = _udp.Receive(ref remote);

                    if (buf == null || buf.Length == 0)
                        continue;

                    if (_udpRemote == null)
                    {
                        _udpRemote = remote;
                        System.Diagnostics.Debug.WriteLine($"[UDP] Remote lock: {remote.Address}:{remote.Port}");
                    }

                    if (firstPacket)
                    {
                        firstPacket = false;
                        SetStatus("Durum: UDP MAVLink → Telemetri alınıyor.", Brushes.LimeGreen);
                    }

                    HandleMavlinkBytes(buf, buf.Length);
                }
                catch (SocketException ex)
                {
                    System.Diagnostics.Debug.WriteLine("UDP recv socket err: " + ex.Message);
                    if (ex.SocketErrorCode != SocketError.TimedOut)
                    {
                        SetStatus("Durum: UDP okuma hatası → " + ex.Message, Brushes.OrangeRed);
                        Thread.Sleep(200);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("UDP recv err: " + ex.Message);
                    SetStatus("Durum: UDP okuma hatası → " + ex.Message, Brushes.OrangeRed);
                    Thread.Sleep(200);
                }
            }

            SetStatus("Durum: UDP MAVLink döngüsü bitti (kapandı).", Brushes.Gray);
        }

        private void HandleMavlinkBytes(byte[] buf, int n)
        {
            _mavReader.Feed(buf, n, msg =>
            {
                if (msg.MsgId == 0 && msg.Payload is { Length: >= 9 })
                {
                    const byte MAV_TYPE_FIXED_WING = 1;
                    byte type = msg.Payload[4];

                    if (!_haveTarget && type == MAV_TYPE_FIXED_WING)
                    {
                        _targetSys = msg.SysId;
                        _targetComp = msg.CompId;
                        _haveTarget = true;
                        _mav?.UpdateTarget(_targetSys, _targetComp);
                        SetStatus($"Durum: HEARTBEAT → sys={_targetSys}, comp={_targetComp}", Brushes.LimeGreen);
                        System.Diagnostics.Debug.WriteLine($"[LOCK] Fixed-wing target: sys={_targetSys}, comp={_targetComp}");
                    }
                }

                if (msg.MsgId == 77 && msg.Payload is { Length: >= 3 })
                {
                    ushort cmd = BitConverter.ToUInt16(msg.Payload, 0);
                    byte result = msg.Payload[2];
                    System.Diagnostics.Debug.WriteLine($"[ACK] cmd={cmd} result={result}");
                }

                if (msg.MsgId == 253 && msg.Payload is { Length: > 0 })
                {
                    byte sev = msg.Payload[0];
                    var text = System.Text.Encoding.ASCII
                        .GetString(msg.Payload, 1, msg.Payload.Length - 1)
                        .TrimEnd('\0');
                    System.Diagnostics.Debug.WriteLine($"[TXT] {sev}: {text}");
                }

                if (msg.MsgId == 22 && msg.Payload is { Length: 25 })
                {
                    float value = BitConverter.ToSingle(msg.Payload, 0);
                    ushort count = BitConverter.ToUInt16(msg.Payload, 4);
                    ushort index = BitConverter.ToUInt16(msg.Payload, 6);
                    string id = ExtractCStr(msg.Payload, 8, 16);

                    ParamFeed.Push(
                        id,
                        value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "",
                        ""
                    );

                    lock (_paramLock)
                    {
                        _lastParamAt = DateTime.UtcNow;

                        if (_paramCount < 0)
                            _paramCount = count;

                        _gotIdx.Add(index);

                        if (_paramCount > 0 && _gotIdx.Count >= _paramCount)
                        {
                            _paramDownloading = false;
                            Task.Run(async () =>
                            {
                                await Task.Delay(120);
                                ParamFeed.Complete();
                            });
                        }
                    }
                }

                _last = MavToTelemetryMapper.UpdateFrom(msg, _last);
                TelemetryHub.Instance.Publish(_last);
            });
        }

        private void OnRequestAllParams()
        {
            lock (_paramLock)
            {
                if (_paramDownloading) return;
                _paramDownloading = true;
                _paramCount = -1;
                _gotIdx.Clear();
                _lastParamAt = DateTime.MinValue;
            }

            Task.Run(async () =>
            {
                await Task.Delay(_haveTarget ? 0 : 400);
                var sys = _haveTarget ? _targetSys : (byte)1;
                var cp = _haveTarget ? _targetComp : (byte)1;
                SendParamRequestList(sys, cp);

                await Task.Delay(1500);

                bool needRetry;
                lock (_paramLock)
                {
                    needRetry = _gotIdx.Count == 0 && _paramDownloading;
                }

                if (needRetry)
                    SendParamRequestList(sys, cp);
            });
        }

        private void SendParamRequestList(byte targetSys, byte targetComp)
        {
            byte[] payload = new byte[2] { targetSys, targetComp };
            byte[] pkt = BuildV2Packet(21, 255, 190, ref _seqTx, payload, CRC_EXTRA_PARAM_REQUEST_LIST);

            bool wrote = false;
            try
            {
                if (_serial is { IsOpen: true })
                {
                    _serial.Write(pkt, 0, pkt.Length);
                    wrote = true;
                }
            }
            catch { }

            if (!wrote)
            {
                try
                {
                    if (_udp != null && _udpRemote != null)
                        _udp.Send(pkt, pkt.Length, _udpRemote);
                }
                catch { }
            }
        }

        private static string ExtractCStr(byte[] src, int offset, int maxLen)
        {
            int end = offset;
            int lim = Math.Min(src.Length, offset + maxLen);

            while (end < lim && src[end] != 0)
                end++;

            return System.Text.Encoding.ASCII.GetString(src, offset, end - offset);
        }

        private static ushort X25(byte[] buf, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
                crc = Accumulate(crc, buf[offset + i]);
            return crc;
        }

        private static ushort Accumulate(ushort crc, byte b)
        {
            unchecked
            {
                b ^= (byte)(crc & 0xFF);
                b ^= (byte)(b << 4);
                return (ushort)(((b << 8) | ((crc >> 8) & 0xFF)) ^ (byte)(b >> 4) ^ (b << 3));
            }
        }

        private byte[] BuildV2Packet(uint msgId, byte sysId, byte compId, ref byte seq, byte[] payload, byte extraCrc)
        {
            byte len = (byte)payload.Length;
            byte[] pkt = new byte[10 + len + 2];

            pkt[0] = MAV_V2;
            pkt[1] = len;
            pkt[2] = 0;
            pkt[3] = 0;
            pkt[4] = seq++;
            pkt[5] = sysId;
            pkt[6] = compId;
            pkt[7] = (byte)(msgId & 0xFF);
            pkt[8] = (byte)((msgId >> 8) & 0xFF);
            pkt[9] = (byte)((msgId >> 16) & 0xFF);

            Buffer.BlockCopy(payload, 0, pkt, 10, len);

            ushort crc = X25(pkt, 1, 9 + len);
            crc = Accumulate(crc, extraCrc);

            pkt[10 + len] = (byte)(crc & 0xFF);
            pkt[11 + len] = (byte)((crc >> 8) & 0xFF);

            return pkt;
        }

        private void TryBindCommandsToData()
        {
        }

        private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaxBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized)
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void RefreshCom_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rotateAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8))
                {
                    EasingFunction = new CircleEase { EasingMode = EasingMode.EaseInOut }
                };
                RefreshRotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);

                string prevPort = ComPortCombo.SelectedValue as string;

                var list = new List<ComItem>();
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var name = mo["Name"]?.ToString() ?? "";
                        int i1 = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
                        int i2 = name.LastIndexOf(')');
                        if (i1 >= 0 && i2 > i1)
                        {
                            var port = name.Substring(i1 + 1, i2 - (i1 + 1));
                            list.Add(new ComItem { Port = port, Label = $"{port} — {name[..i1].Trim()}" });
                        }
                    }
                }
                catch { }

                var onlyPorts = SerialPort.GetPortNames()
                    .OrderBy(p => (p.StartsWith("COM") && int.TryParse(p[3..], out var n)) ? n : int.MaxValue)
                    .ToArray();

                foreach (var p in onlyPorts)
                    if (!list.Any(x => x.Port.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new ComItem { Port = p, Label = p });

                ComPortCombo.ItemsSource = list;
                ComPortCombo.DisplayMemberPath = "Label";
                ComPortCombo.SelectedValuePath = "Port";

                if (!string.IsNullOrWhiteSpace(prevPort) &&
                    list.Any(x => x.Port.Equals(prevPort, StringComparison.OrdinalIgnoreCase)))
                {
                    ComPortCombo.SelectedValue = prevPort;
                }
                else if (list.Count > 0)
                {
                    ComPortCombo.SelectedIndex = 0;
                }
                else
                {
                    ComPortCombo.SelectedItem = null;
                    ComPortCombo.Text = string.Empty;
                }

                SetStatus("Durum: COM port listesi yenilendi.", Brushes.Gray);
            }
            catch (Exception ex)
            {
                MessageBox.Show("COM portları yenilenemedi:\n" + ex.Message,
                                "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Durum: COM yenileme hatası → " + ex.Message, Brushes.OrangeRed);
            }
        }

        private void PanelsMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi) return;
            if (mi.Tag is not string key) return;

            if (CONTENT_HOST?.Content is arayuz_deneme_1.data view)
            {
                view.SetPanelVisible(key, mi.IsChecked);
            }
        }
    }
}