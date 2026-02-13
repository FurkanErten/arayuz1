// TelemetryHub.cs — SSE + Stream (COM/TCP) destekli tekil dağıtıcı (thread-safe)
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace arayuz_deneme_1
{
    public enum TelemetrySourceMode { None, Sse, Stream }

    public interface ITelemetryDecoder
    {
        void OnBytes(byte[] buffer, int count, Action<TelemetryDto> emit);
        void OnCompleted(Action<TelemetryDto> emit);
    }

    public sealed class NdjsonTelemetryDecoder : ITelemetryDecoder
    {
        private readonly MemoryStream _ms = new();
        public void OnBytes(byte[] buffer, int count, Action<TelemetryDto> emit)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    int len = i - start;
                    if (len > 0) EmitLine(buffer, start, len, emit);
                    start = i + 1;
                }
            }
            if (start < count) _ms.Write(buffer, start, count - start);
        }

        public void OnCompleted(Action<TelemetryDto> emit)
        {
            if (_ms.Length == 0) return;
            try
            {
                var span = _ms.ToArray();
                EmitSpan(span, emit);
            }
            catch { }
            finally { _ms.SetLength(0); }
        }

        private void EmitLine(byte[] buffer, int offset, int count, Action<TelemetryDto> emit)
        {
            if (_ms.Length > 0)
            {
                _ms.Write(buffer, offset, count);
                var merged = _ms.ToArray();
                EmitSpan(merged, emit);
                _ms.SetLength(0);
                return;
            }
            var span = new ReadOnlySpan<byte>(buffer, offset, count);
            TryParse(span, emit);
        }

        private static void EmitSpan(byte[] bytes, Action<TelemetryDto> emit) => TryParse(bytes, emit);

        private static void TryParse(ReadOnlySpan<byte> utf8Json, Action<TelemetryDto> emit)
        {
            try
            {
                if (utf8Json.Length > 0 && utf8Json[^1] == (byte)'\r') utf8Json = utf8Json[..^1];
                var dto = JsonSerializer.Deserialize<TelemetryDto>(utf8Json);
                if (dto != null) emit(dto);
            }
            catch (Exception ex) { Debug.WriteLine("[NdjsonDecoder] JSON parse hatası: " + ex.Message); }
        }
    }

    public sealed class TelemetryHub : IDisposable
    {
        public static TelemetryHub Instance { get; } = new TelemetryHub();
        private TelemetryHub() { }

        private readonly object _gate = new();

        private TelemetrySourceMode _mode = TelemetrySourceMode.None;

        private SseClient? _sse;
        public string TelemetryUrl { get; set; } = "http://127.0.0.1:5006/telemetry";

        private Stream? _stream;
        private ITelemetryDecoder? _decoder;

        private CancellationTokenSource? _cts;
        private Task? _readerTask;

        // Son paket snapshot
        private volatile TelemetryDto? _last;

        public event Action<TelemetryDto>? OnPacket;
        public event Action<Exception>? OnError;

        public async Task EnsureStartedAsync()
        {
            //await BackendHost.EnsureStartedAsync().ConfigureAwait(false);

            lock (_gate)
            {
                if (_mode == TelemetrySourceMode.Sse && _readerTask != null && !_readerTask.IsCompleted)
                    return;

                Cleanup_NoLock();

                _mode = TelemetrySourceMode.Sse;
                _cts = new CancellationTokenSource();

                _sse = new SseClient(TelemetryUrl);
                _sse.OnPacket += HandlePacketSafe;
                _sse.OnError += HandleErrorSafe;

                _readerTask = Task.Run(async () =>
                {
                    try { await _sse!.StartAsync().ConfigureAwait(false); }
                    catch (Exception ex) { HandleErrorSafe(ex); }
                }, _cts.Token);
            }
        }

        public Task StartSseAsync(string? url = null)
        {
            if (!string.IsNullOrWhiteSpace(url)) TelemetryUrl = url!;
            return EnsureStartedAsync();
        }

        public void Attach(Stream stream, ITelemetryDecoder? decoder = null)
        {
            lock (_gate)
            {
                Cleanup_NoLock();

                _mode = TelemetrySourceMode.Stream;
                _stream = stream;
                _decoder = decoder ?? new NdjsonTelemetryDecoder();

                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                _readerTask = Task.Run(async () =>
                {
                    byte[] buf = new byte[4096];
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), token).ConfigureAwait(false);
                            if (n <= 0) break;
                            _decoder!.OnBytes(buf, n, HandlePacketSafe);
                        }
                        _decoder!.OnCompleted(HandlePacketSafe);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { HandleErrorSafe(ex); }
                }, token);
            }
        }

        public void Detach() => Stop();

        public void Stop()
        {
            lock (_gate)
            {
                Cleanup_NoLock();
                _mode = TelemetrySourceMode.None;
            }
        }

        public void Dispose() => Stop();

        private void HandlePacketSafe(TelemetryDto dto)
        {
            try
            {
                _last = dto;                 // snapshot
                OnPacket?.Invoke(dto);
            }
            catch (Exception ex) { Debug.WriteLine("[TelemetryHub][OnPacket] " + ex); }
        }

        private void HandleErrorSafe(Exception ex)
        {
            try
            {
                Debug.WriteLine("[TelemetryHub][OnError] " + ex.Message);
                OnError?.Invoke(ex);
            }
            catch (Exception inner) { Debug.WriteLine("[TelemetryHub][OnError handler] " + inner); }
        }

        private void Cleanup_NoLock()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;

            if (_sse != null)
            {
                try { _sse.OnPacket -= HandlePacketSafe; } catch { }
                try { _sse.OnError -= HandleErrorSafe; } catch { }
                try { _sse.Stop(); } catch { }
                try { _sse.Dispose(); } catch { }
                _sse = null;
            }

            try { _stream?.Dispose(); } catch { }
            _stream = null;
            _decoder = null;
            _readerTask = null;
        }

        // Harici üreticiler (MAV parser vb.) buradan telemetri basabilir
        public void Publish(TelemetryDto dto)
        {
            try
            {
                _last = dto;                 // snapshot
                OnPacket?.Invoke(dto);
            }
            catch { }
        }

        // Snapshot API
        public bool TryGetLast(out TelemetryDto dto)
        {
            var s = _last;
            if (s != null) { dto = s; return true; }
            dto = new TelemetryDto();
            return false;
        }

        public TelemetryDto? Last => _last;
    }
}
