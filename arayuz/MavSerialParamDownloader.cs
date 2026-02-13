// MavSerialParamDownloader.cs (güncel ve dayanıklı)
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace arayuz_deneme_1
{
    public sealed class MavSerialParamDownloader : IDisposable
    {
        private readonly SerialPort _port = new();
        private CancellationTokenSource? _cts;
        private Task? _rxTask;

        private byte _seqTx;
        private byte _targetSys;   // HEARTBEAT ile öğrenilecek
        private byte _targetComp;  // HEARTBEAT ile öğrenilecek
        private volatile bool _haveTarget;

        // RX buffer
        private readonly byte[] _rxBuf = new byte[8192];
        private int _rxLen;

        // MAVLink sabitleri / extra CRC
        private const byte MAV_V1 = 0xFE;
        private const byte MAV_V2 = 0xFD;

        private const byte CRC_EXTRA_HEARTBEAT = 50;             // msg 0
        private const byte CRC_EXTRA_PARAM_REQUEST_LIST = 159;   // msg 21
        private const byte CRC_EXTRA_PARAM_VALUE = 220;          // msg 22

        // Param indirme durumu
        private readonly object _stateLock = new();
        private bool _paramDownloading;
        private int _paramCount = -1;
        private readonly HashSet<int> _gotIdx = new(); // alınan param_index seti
        private DateTime _lastParamAt = DateTime.MinValue;
        private Task? _retryTask;

        public void Start(string portName, int baud)
        {
            Stop();

            _port.PortName = portName;
            _port.BaudRate = baud;
            _port.Parity = Parity.None;
            _port.StopBits = StopBits.One;
            _port.DataBits = 8;
            _port.Handshake = Handshake.None;
            _port.ReadTimeout = 50;
            _port.WriteTimeout = 2000;
            _port.DtrEnable = false;
            _port.RtsEnable = false;

            _port.Open();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _rxTask = Task.Run(() => RxLoop(ct), ct);

            // UI/ConfigPanel "tam liste iste" dediğinde dinle
            ParamFeed.OnRequestAll -= OnRequestAll; // double-subscribe koruması
            ParamFeed.OnRequestAll += OnRequestAll;
        }

        public void Stop()
        {
            try { ParamFeed.OnRequestAll -= OnRequestAll; } catch { /* yut */ }

            try { _cts?.Cancel(); } catch { }
            try { _rxTask?.Wait(250); } catch { }

            try { if (_port.IsOpen) _port.Close(); } catch { }

            _cts?.Dispose(); _cts = null;
            _rxTask = null;

            lock (_stateLock)
            {
                _haveTarget = false;
                _targetSys = _targetComp = 0;
                _rxLen = 0;

                _paramDownloading = false;
                _paramCount = -1;
                _gotIdx.Clear();
                _lastParamAt = DateTime.MinValue;
            }
        }

        public void Dispose() => Stop();

        // ================= RX LOOP =================
        private void RxLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_port.IsOpen) { Thread.Sleep(50); continue; }

                    int n = _port.BytesToRead;
                    if (n == 0) { Thread.Sleep(5); continue; }

                    if (_rxLen + n > _rxBuf.Length) _rxLen = 0; // overflow koruma
                    int read = _port.Read(_rxBuf, _rxLen, Math.Min(n, _rxBuf.Length - _rxLen));
                    _rxLen += read;

                    int consumed = 0;
                    while (TryParseOne(_rxBuf, _rxLen, out int used, out MavMsg msg))
                    {
                        consumed += used;
                        HandleMessage(msg);
                    }

                    if (consumed > 0)
                    {
                        // kalan baytları başa kaydır
                        Buffer.BlockCopy(_rxBuf, consumed, _rxBuf, 0, _rxLen - consumed);
                        _rxLen -= consumed;
                    }
                }
                catch (TimeoutException) { /* normal */ }
                catch
                {
                    // Port/okuma hatası — kısa nefes al
                    Thread.Sleep(50);
                }
            }
        }

        // ============== MAVLink parsing (v1/v2 minimalist) ==============
        private struct MavMsg
        {
            public byte Version;    // 0xFE/0xFD
            public byte Seq;
            public byte SysId;
            public byte CompId;
            public uint MsgId;      // v1: 1 byte, v2: 3 byte
            public byte[] Payload;  // exact len
        }

        private static bool TryParseOne(byte[] buf, int len, out int used, out MavMsg msg)
        {
            used = 0; msg = default;

            int i = 0;
            // magic byte ara
            while (i < len && buf[i] != MAV_V1 && buf[i] != MAV_V2) i++;
            if (i >= len) return false; // magic yok
            if (len - i < 8) return false; // min header yok

            byte magic = buf[i];
            if (magic == MAV_V1)
            {
                byte payloadLen = buf[i + 1];
                if (len - i < 6 + payloadLen + 2) return false; // hdr(6) + payload + crc(2)
                byte seq = buf[i + 2];
                byte sys = buf[i + 3];
                byte comp = buf[i + 4];
                byte msgid = buf[i + 5];

                // CRC kontrol
                ushort crc = X25(buf, i + 1, 5 + payloadLen); // len..payload
                crc = Accumulate(crc, CRCExtraFor(msgid));
                ushort crcPkt = (ushort)(buf[i + 6 + payloadLen] | (buf[i + 7 + payloadLen] << 8));
                if (crc != crcPkt) { used = i + 1; return true; } // bu magic'i geç

                var payload = new byte[payloadLen];
                Buffer.BlockCopy(buf, i + 6, payload, 0, payloadLen);

                msg = new MavMsg
                {
                    Version = MAV_V1,
                    Seq = seq,
                    SysId = sys,
                    CompId = comp,
                    MsgId = msgid,
                    Payload = payload
                };
                used = i + 6 + payloadLen + 2;
                return true;
            }
            else // MAV_V2
            {
                if (len - i < 10) return false;
                byte payloadLen = buf[i + 1];
                byte incFlags = buf[i + 2];
                // byte compFlags = buf[i + 3];
                byte seq = buf[i + 4];
                byte sys = buf[i + 5];
                byte comp = buf[i + 6];
                uint msgid = (uint)(buf[i + 7] | (buf[i + 8] << 8) | (buf[i + 9] << 16));

                int frameLen = 10 + payloadLen + 2; // hdr(10)+payload+crc
                bool hasSignature = (incFlags & 0x01) != 0;
                if (hasSignature) frameLen += 13;

                if (len - i < frameLen) return false;

                // CRC
                ushort crc = X25(buf, i + 1, 9 + payloadLen); // len..payload (v2)
                crc = Accumulate(crc, CRCExtraFor(msgid));
                ushort crcPkt = (ushort)(buf[i + 10 + payloadLen] | (buf[i + 11 + payloadLen] << 8));
                if (crc != crcPkt) { used = i + 1; return true; }

                var payload = new byte[payloadLen];
                Buffer.BlockCopy(buf, i + 10, payload, 0, payloadLen);

                msg = new MavMsg
                {
                    Version = MAV_V2,
                    Seq = seq,
                    SysId = sys,
                    CompId = comp,
                    MsgId = msgid,
                    Payload = payload
                };
                used = i + frameLen;
                return true;
            }
        }

        private static byte CRCExtraFor(uint msgid) =>
            msgid switch
            {
                0 => CRC_EXTRA_HEARTBEAT,
                21 => CRC_EXTRA_PARAM_REQUEST_LIST,
                22 => CRC_EXTRA_PARAM_VALUE,
                _ => (byte)0
            };

        private static ushort X25(byte[] buf, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++) crc = Accumulate(crc, buf[offset + i]);
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

        // ================= Handle messages =================
        private void HandleMessage(MavMsg m)
        {
            switch (m.MsgId)
            {
                case 0: // HEARTBEAT
                    if (!_haveTarget)
                    {
                        _targetSys = m.SysId;
                        _targetComp = m.CompId; // genelde 1
                        _haveTarget = true;
                    }
                    break;

                case 22: // PARAM_VALUE (ArduPilot v2: len=25 -> value, count, index, id[16], type)
                    if (m.Payload is { Length: 25 })
                    {
                        float value = BitConverter.ToSingle(m.Payload, 0);
                        ushort count = BitConverter.ToUInt16(m.Payload, 4);
                        ushort index = BitConverter.ToUInt16(m.Payload, 6);
                        string id = ExtractCStr(m.Payload, 8, 16);

                        // UI'ya akıt
                        ParamFeed.Push(id,
                            value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "", "");

                        lock (_stateLock)
                        {
                            _lastParamAt = DateTime.UtcNow;
                            if (_paramCount < 0) _paramCount = count;
                            _gotIdx.Add(index);

                            // Tüm paramlar geldi mi?
                            if (_paramCount > 0 && _gotIdx.Count >= _paramCount)
                            {
                                _paramDownloading = false;
                                // Debounce: küçük gecikmeyle Complete (tekrarlayan paketler gelse bile)
                                Task.Run(async () =>
                                {
                                    await Task.Delay(150);
                                    ParamFeed.Complete();
                                });
                            }
                        }
                    }
                    else if (m.Payload is { Length: 23 })
                    {
                        // Bazı v1 varyantları: value(4), id[16], type(1), count(2) — nadir
                        string id = ExtractCStr(m.Payload, 4, 16);
                        float value = BitConverter.ToSingle(m.Payload, 0);
                        ParamFeed.Push(id, value.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "");

                        lock (_stateLock)
                        {
                            _lastParamAt = DateTime.UtcNow;
                            // count/index bilgimiz yoksa sadece alındı sayıyoruz
                            _gotIdx.Add(_gotIdx.Count);
                        }
                    }
                    break;
            }
        }

        private static string ExtractCStr(byte[] src, int offset, int maxLen)
        {
            int end = offset, lim = Math.Min(src.Length, offset + maxLen);
            while (end < lim && src[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(src, offset, end - offset);
        }

        // ================= Send helpers =================
        private void OnRequestAll()
        {
            lock (_stateLock)
            {
                // Zaten indiriyorsa tekrar tetikleme (throttle)
                if (_paramDownloading) return;

                _paramDownloading = true;
                _paramCount = -1;
                _gotIdx.Clear();
                _lastParamAt = DateTime.MinValue;
            }

            // Hedef yoksa kısa bekleyip gönder
            if (!_haveTarget)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(400);
                    if (_haveTarget) SendParamRequestList(_targetSys, _targetComp);
                    else SendParamRequestList(1, 1); // fallback: SYS=1, COMP=1
                    StartRetryLoop();
                });
            }
            else
            {
                SendParamRequestList(_targetSys, _targetComp);
                StartRetryLoop();
            }
        }

        private void StartRetryLoop()
        {
            // 1.5 s içinde hiç param gelmezse yeniden iste (max 3 deneme)
            _retryTask = Task.Run(async () =>
            {
                const int maxRetries = 3;
                for (int i = 0; i < maxRetries; i++)
                {
                    await Task.Delay(1500);
                    bool needRetry;
                    lock (_stateLock)
                    {
                        // İndirme bitmişse ya da bir şeyler gelmişse devam etme
                        if (!_paramDownloading) return;
                        needRetry = _gotIdx.Count == 0;
                    }
                    if (needRetry)
                    {
                        SendParamRequestList(_haveTarget ? _targetSys : (byte)1,
                                             _haveTarget ? _targetComp : (byte)1);
                    }
                    else break; // veri akışı başladı
                }

                // Eğer paramCount biliniyor ve uzun süre ilerleme yoksa Complete ile kapat
                await Task.Delay(1000);
                lock (_stateLock)
                {
                    if (_paramDownloading && _paramCount > 0 && _gotIdx.Count > 0)
                    {
                        _paramDownloading = false;
                        ParamFeed.Complete();
                    }
                }
            });
        }

        private void SendParamRequestList(byte targetSys, byte targetComp)
        {
            // payload: uint8 target_system, uint8 target_component
            byte[] payload = new byte[2] { targetSys, targetComp };
            byte[] packet = BuildV2Packet(msgId: 21, sysId: 255, compId: 190, ref _seqTx, payload, CRC_EXTRA_PARAM_REQUEST_LIST);
            SafeWrite(packet);
        }

        private byte[] BuildV2Packet(uint msgId, byte sysId, byte compId, ref byte seq, byte[] payload, byte extraCrc)
        {
            byte len = (byte)payload.Length;
            byte[] pkt = new byte[10 + len + 2]; // signature yok
            pkt[0] = MAV_V2;
            pkt[1] = len;
            pkt[2] = 0x00; // incompat flags (signature yok)
            pkt[3] = 0x00; // compat flags
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

        private void SafeWrite(byte[] data)
        {
            try { if (_port.IsOpen) _port.Write(data, 0, data.Length); }
            catch { /* port kapalı olabilir */ }
        }
    }
}
