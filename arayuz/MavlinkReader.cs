// MavlinkReader.cs — v1/v2 parser (düzeltilmiş, debug-friendly)
// arayuz_deneme_1
using System;
using System.Collections.Generic;

namespace arayuz_deneme_1
{
    public class MavlinkReader
    {
        private readonly List<byte> _rx = new();
        private const int MAX_FRAME = 300; // payload+header+crc+sig üst sınır

        public void Feed(byte[] data, int count, Action<MavMessage> onMsg)
        {
            if (data == null || count <= 0) return;
            for (int i = 0; i < count; i++)
                _rx.Add(data[i]);

            int idx = 0;
            while (idx < _rx.Count)
            {
                byte stx = _rx[idx];
                if (stx != 0xFE && stx != 0xFD)
                {
                    // sync kaybolduysa, bir sonraki STX'e atla
                    int next = _rx.FindIndex(idx + 1, b => b == 0xFE || b == 0xFD);
                    idx = next >= 0 ? next : _rx.Count;
                    continue;
                }

                // ================= MAVLink v1 =================
                if (stx == 0xFE)
                {
                    if (_rx.Count - idx < 6) break; // header eksik
                    byte len = _rx[idx + 1];
                    if (len > 255) { idx++; continue; }

                    int total = 6 + len + 2; // hdr + payload + CRC
                    if (total > MAX_FRAME) { idx++; continue; }
                    if (_rx.Count - idx < total) break; // frame eksik

                    byte sys = _rx[idx + 3];
                    byte comp = _rx[idx + 4];
                    byte msgid = _rx[idx + 5];
                    var payload = new byte[len];
                    _rx.CopyTo(idx + 6, payload, 0, len);

                    System.Diagnostics.Debug.WriteLine($"[DBG] v1 msg={msgid} len={len}");

                    onMsg?.Invoke(new MavMessage(1, sys, comp, msgid, payload));
                    idx += total;
                    continue;
                }

                // ================= MAVLink v2 =================
                if (stx == 0xFD)
                {
                    if (_rx.Count - idx < 10) break;
                    byte v2len = _rx[idx + 1];
                    if (v2len > 255) { idx++; continue; }

                    byte incompat = _rx[idx + 2];
                    byte sys2 = _rx[idx + 5];
                    byte comp2 = _rx[idx + 6];
                    uint msgid2 = (uint)(_rx[idx + 7] | (_rx[idx + 8] << 8) | (_rx[idx + 9] << 16));

                    int sigLen = ((incompat & 0x01) != 0) ? 13 : 0;
                    int v2total = 12 + v2len + sigLen; // 10 header + payload + 2 CRC + opt.sig

                    if (v2total > MAX_FRAME) { idx++; continue; }
                    if (_rx.Count - idx < v2total) break;

                    var v2payload = new byte[v2len];
                    _rx.CopyTo(idx + 10, v2payload, 0, v2len);

                    System.Diagnostics.Debug.WriteLine($"[DBG] v2 msg={msgid2} len={v2len}");

                    onMsg?.Invoke(new MavMessage(2, sys2, comp2, msgid2, v2payload));
                    idx += v2total;
                    continue;
                }

                idx++;
            }

            // tüketilenleri buffer’dan at
            if (idx > 0)
            {
                _rx.RemoveRange(0, idx);
                if (_rx.Capacity > 4096 && _rx.Count < 1024)
                    _rx.TrimExcess();
            }
        }
    }
}
