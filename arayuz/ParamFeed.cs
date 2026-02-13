using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace arayuz_deneme_1
{
    /// MAVLink param taşımak için arayüz (senin oturum sınıfın bunu uygulasın).
    public interface IParamTransport
    {
        event Action<string /*name*/, float /*value*/, byte /*paramType*/>? ParamValue;
        event Action? ParamListComplete;

        Task SendParamRequestListAsync(CancellationToken ct = default);
        Task SendParamSetAsync(string name, float value, byte paramType, CancellationToken ct = default);
    }

    /// UI ↔ taşıma katmanı arasında ortak feed.
    public static class ParamFeed
    {
        // UI sinyalleri
        public static event Action? OnRequestAll;
        public static event Action<string, string, string?, string?, byte?>? OnParam; // (name, valueStr, units, desc, type)
        public static event Action? OnComplete;

        public static void Complete() => OnComplete?.Invoke();

        // Transport
        private static readonly object _transportLock = new();
        private static IParamTransport? _transport;

        public static void SetTransport(IParamTransport transport)
        {
            lock (_transportLock)
            {
                if (_transport != null)
                {
                    _transport.ParamValue -= OnTransportParamValue;
                    _transport.ParamListComplete -= OnTransportParamListComplete;
                }
                _transport = transport;
                _transport.ParamValue += OnTransportParamValue;
                _transport.ParamListComplete += OnTransportParamListComplete;
            }
        }

        private static void OnTransportParamValue(string name, float value, byte paramType)
        {
            OnParam?.Invoke(name, ToInvariant(value), null, null, paramType);
            TryCompleteEcho(name, value, paramType);
        }

        private static void OnTransportParamListComplete() => OnComplete?.Invoke();

        // === UI tarafından çağrılanlar ===
        public static void RequestAll()
        {
            OnRequestAll?.Invoke();

            IParamTransport? t;
            lock (_transportLock) t = _transport;
            if (t == null) return;

            _ = Task.Run(() => t.SendParamRequestListAsync());
        }

        public static Task SendParamSetAsync(string name, float value, byte paramType)
        {
            IParamTransport? t;
            lock (_transportLock) t = _transport;
            if (t == null) throw new InvalidOperationException("ParamFeed: transport set edilmedi. SetTransport(...) çağırın.");
            return t.SendParamSetAsync(name, value, paramType);
        }

        /// Belirli isimde PARAM_VALUE echosunu bekler (timeout ms).
        public static async Task<(string Name, float Value, byte ParamType)?> WaitParamValueAsync(string name, int timeoutMs)
        {
            var key = Key(name);
            var tcs = new TaskCompletionSource<(string, float, byte)>(TaskCreationOptions.RunContinuationsAsynchronously);

            var list = _waiters.GetOrAdd(key, _ => new List<TaskCompletionSource<(string, float, byte)>>());
            lock (list) list.Add(tcs);

            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(() =>
            {
                lock (list) list.Remove(tcs);
                tcs.TrySetResult(default); // timeout → null
            });

            var result = await tcs.Task.ConfigureAwait(false);
            return result == default ? null : result;
        }

        /// Test/manuel besleme.
        public static void Push(string name, string value, string? units = null, string? desc = null, byte? paramType = null)
        {
            OnParam?.Invoke(name, value, units, desc, paramType);
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ||
                float.TryParse(value, out f))
            {
                TryCompleteEcho(name, f, paramType ?? 9);
            }
        }


        // === Echo bekleme altyapısı ===
        private static readonly ConcurrentDictionary<string, List<TaskCompletionSource<(string, float, byte)>>> _waiters
            = new(StringComparer.OrdinalIgnoreCase);

        private static void TryCompleteEcho(string name, float value, byte paramType)
        {
            var key = Key(name);
            if (!_waiters.TryGetValue(key, out var list)) return;

            lock (list)
            {
                // tüm bekleyenleri uyandır
                foreach (var tcs in list.ToArray())
                {
                    tcs.TrySetResult((name, value, paramType));
                    list.Remove(tcs);
                }
                if (list.Count == 0) _waiters.TryRemove(key, out _);
            }
        }

        private static string Key(string name) => name?.Trim() ?? string.Empty;
        private static string ToInvariant(float f) => f.ToString("0.########", CultureInfo.InvariantCulture);
    }
}
