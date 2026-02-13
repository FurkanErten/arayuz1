// SseClient.cs — iptal güvenli, defansif JSON, NRE race fix
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace arayuz_deneme_1
{
    public sealed class SseClient : IDisposable
    {
        private readonly string _url;
        private readonly HttpClient _http =
            new(new HttpClientHandler { AllowAutoRedirect = true })
            { Timeout = Timeout.InfiniteTimeSpan };

        private CancellationTokenSource? _cts;
        private Task? _readerTask;

        public event Action<TelemetryDto>? OnPacket;
        public event Action<Exception>? OnError;

        public SseClient(string url)
        {
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
            // İstersen: _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        }

        public Task StartAsync()
        {
            Stop(); // varsa kapat

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, cts);
            var token = cts.Token; // yerel token

            _readerTask = Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, _url);
                    using var resp = await _http
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);

                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);

#if NET8_0_OR_GREATER
                    // .NET 8: iptal destekli ReadLineAsync
                    var dataBuf = new StringBuilder();
                    while (!token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                        if (line is null) break;

                        if (line.Length == 0)
                        {
                            // event sonu
                            var payload = dataBuf.ToString().Trim();
                            dataBuf.Clear();
                            if (payload.Length > 0) ParseAndEmit(payload);
                        }
                        else if (line.StartsWith("data:", StringComparison.Ordinal))
                        {
                            dataBuf.Append(line.AsSpan(5).ToString().TrimStart()).Append('\n');
                        }
                        // SSE yorum/heartbeat satırı (":" ile başlar) ya da diğer alanlar: yok say
                    }
#else
                    // .NET 7 ve öncesi: iptal için stream kapanmasına güveniyoruz
                    var dataBuf = new StringBuilder();
                    while (!token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line is null) break;

                        if (line.Length == 0)
                        {
                            var payload = dataBuf.ToString().Trim();
                            dataBuf.Clear();
                            if (payload.Length > 0) ParseAndEmit(payload);
                        }
                        else if (line.StartsWith("data:", StringComparison.Ordinal))
                        {
                            dataBuf.Append(line.AsSpan(5).ToString().TrimStart()).Append('\n');
                        }
                    }
#endif
                }
                catch (OperationCanceledException)
                {
                    // normal kapanış
                }
                catch (ObjectDisposedException)
                {
                    // kapanış yarışı olabilir
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex);
                }
            }, token);

            return _readerTask!;
        }

        private void ParseAndEmit(string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var r = doc.RootElement;

                double GetD(string name, double def = double.NaN)
                    => r.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : def;

                int GetI(string name, int def = 0)
                    => r.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : def;

                bool GetB(string name, bool def = false)
                    => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.False ? v.GetBoolean() : def);

                string GetS(string name, string def = "")
                    => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? def) : def;

                var dto = new TelemetryDto
                {
                    pitchDeg = GetD("pitchDeg"),
                    rollDeg = GetD("rollDeg"),
                    headingDeg = GetD("headingDeg"),
                    airspeed = GetD("airspeed"),
                    altitude = GetD("altitude"),
                    groundspeed = GetD("groundspeed"),
                    lat = GetD("lat"),
                    lon = GetD("lon"),
                    sats = GetI("sats"),
                    battVolt = GetD("battVolt"),
                    armed = GetB("armed"),
                    mode = GetS("mode")
                };

                OnPacket?.Invoke(dto);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        }

        public void Stop()
        {
            var old = Interlocked.Exchange(ref _cts, null);
            if (old != null)
            {
                try { old.Cancel(); } catch { }
                try { old.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            Stop();
            _http.Dispose();
        }
    }

    // JsonElement yardımcıları
    internal static class JsonElementExt
    {
        public static bool TryGetDouble(this JsonElement el, out double value)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Number: return el.TryGetDouble(out value);
                case JsonValueKind.String:
                    if (double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
                    break;
            }
            value = default;
            return false;
        }

        public static bool TryGetInt32(this JsonElement el, out int value)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Number: return el.TryGetInt32(out value);
                case JsonValueKind.String:
                    if (int.TryParse(el.GetString(), out value)) return true;
                    break;
            }
            value = default;
            return false;
        }
    }
}
