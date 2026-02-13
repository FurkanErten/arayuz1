// MyMavlinkClient.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace arayuz_deneme_1
{
    public interface IMavlinkClient
    {
        Task SendCommandLongAsync(ushort command,
                                  float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0,
                                  float p5 = 0, float p6 = 0, float p7 = 0,
                                  CancellationToken ct = default);

        Task SetModeAsync(uint customMode,
                          byte baseMode = 1,
                          CancellationToken ct = default);

        Task DoSetModeAsync(uint customMode,
                            byte baseMode = 1,
                            CancellationToken ct = default);

        Task SetArmAsync(bool arm, bool force = false, CancellationToken ct = default);
        Task<bool> IsConnectedAsync();
        void UpdateTarget(byte systemId, byte componentId);

        // >>> BURASI YENİ <<<
        /// <summary>
        /// GUIDED modda uçağı verilen lat/lon/alt (rel) konumuna uçur.
        /// ArduPlane: MAV_CMD_DO_REPOSITION (192)
        /// </summary>
        Task FlyToAsync(double latDeg, double lonDeg, float altRelMeters,
                        CancellationToken ct = default);
    }

    public sealed class MyMavlinkClient : IMavlinkClient
    {
        private readonly Action<byte[]> _writer;
        private readonly Func<bool>? _isConnected;

        private readonly byte _senderSys = 255;
        private readonly byte _senderComp = 190;

        private byte _targetSys = 1, _targetComp = 1;
        private byte _seq;

        private const byte MAV_V2 = 0xFD;

        private const byte CRC_SET_MODE = 89;        // message #11
        private const byte CRC_COMMAND_LONG = 152;   // message #76

        public MyMavlinkClient(Action<byte[]> writer, Func<bool>? isConnected = null)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _isConnected = isConnected;
            _seq = 0;
        }

        public void UpdateTarget(byte systemId, byte componentId)
        {
            _targetSys = systemId;
            _targetComp = componentId;
        }

        public Task<bool> IsConnectedAsync() => Task.FromResult(_isConnected?.Invoke() ?? true);

        public Task SetArmAsync(bool arm, bool force = false, CancellationToken ct = default)
            => SendCommandLongAsync(400 /* MAV_CMD_COMPONENT_ARM_DISARM */,
                                    p1: arm ? 1f : 0f,
                                    p2: force ? 21196f : 0f,
                                    ct: ct);

        public Task SetModeAsync(uint customMode, byte baseMode = 1, CancellationToken ct = default)
        {
            // SET_MODE (message #11): target_system(u8), base_mode(u8), custom_mode(u32 LE)
            var payload = new byte[6];
            payload[0] = _targetSys;
            payload[1] = baseMode;
            BitConverter.GetBytes(customMode).CopyTo(payload, 2);

            SafeWrite(BuildV2Frame(11u, payload, CRC_SET_MODE));
            return Task.CompletedTask;
        }

        // DO_SET_MODE (176) — COMMAND_LONG ile
        public Task DoSetModeAsync(uint customMode, byte baseMode = 1, CancellationToken ct = default)
        {
            // COMMAND_LONG: p1=base_mode, p2=custom_mode
            return SendCommandLongAsync(176 /* MAV_CMD_DO_SET_MODE */,
                                        p1: baseMode,
                                        p2: customMode,
                                        ct: ct);
        }

        public Task SendCommandLongAsync(ushort command,
                                         float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0,
                                         float p5 = 0, float p6 = 0, float p7 = 0,
                                         CancellationToken ct = default)
        {
            var payload = new byte[33];
            int o = 0;
            void Wf(float v) { BitConverter.GetBytes(v).CopyTo(payload, o); o += 4; }
            Wf(p1); Wf(p2); Wf(p3); Wf(p4); Wf(p5); Wf(p6); Wf(p7);

            var cmd = BitConverter.GetBytes(command);
            payload[o++] = cmd[0];
            payload[o++] = cmd[1];
            payload[o++] = _targetSys;
            payload[o++] = _targetComp;
            payload[o++] = 0; // confirmation

            SafeWrite(BuildV2Frame(76u, payload, CRC_COMMAND_LONG));
            return Task.CompletedTask;
        }

        // ===================== FLY TO =====================
        // MAV_CMD_NAV_WAYPOINT (16)
        public Task FlyToAsync(double latDeg, double lonDeg, float altRelMeters,
                       CancellationToken ct = default)
        {
            // ArduPilot / ArduPlane:
            // MAV_CMD_DO_REPOSITION = 192
            // Script örneğindeki gibi:
            //   p1 = -1 (acceptance radius: mevcut değeri kullan)
            //   p4 = NaN (yaw’u elle verme)
            //   p5 = lat, p6 = lon, p7 = alt (relative)
            //
            // NOT: Bu komutun çalışması için uçak GUIDED modda ve havada olmalı.
            // (Mode GUIDED + arm + takeoff’dan sonra dene.)

            float lat = (float)latDeg;
            float lon = (float)lonDeg;

            return SendCommandLongAsync(
                command: 192,        // MAV_CMD_DO_REPOSITION
                p1: -1f,             // acceptance radius (otomatik)
                p2: 0f,
                p3: 0f,
                p4: float.NaN,       // yaw’u elle verme
                p5: lat,             // hedef enlem (deg)
                p6: lon,             // hedef boylam (deg)
                p7: altRelMeters,    // relative alt (m)
                ct: ct
            );
        }

        private byte[] BuildV2Frame(uint msgId, byte[] payload, byte crcExtra)
        {
            byte len = (byte)payload.Length;
            var frame = new byte[10 + len + 2];

            frame[0] = MAV_V2;
            frame[1] = len;
            frame[2] = 0; // incompat
            frame[3] = 0; // compat
            frame[4] = _seq++;
            frame[5] = _senderSys;
            frame[6] = _senderComp;
            frame[7] = (byte)(msgId & 0xFF);
            frame[8] = (byte)((msgId >> 8) & 0xFF);
            frame[9] = (byte)((msgId >> 16) & 0xFF);

            Buffer.BlockCopy(payload, 0, frame, 10, len);

            ushort crc = X25(frame, 1, 9 + len);
            crc = Acc(crc, crcExtra);
            frame[10 + len] = (byte)(crc & 0xFF);
            frame[11 + len] = (byte)((crc >> 8) & 0xFF);

            return frame;
        }

        private void SafeWrite(byte[] data)
        {
            try { _writer(data); }
            catch { }
        }

        private static ushort X25(byte[] buf, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++) crc = Acc(crc, buf[offset + i]);
            return crc;
        }

        private static ushort Acc(ushort crc, byte b)
        {
            unchecked
            {
                b ^= (byte)(crc & 0xFF);
                b ^= (byte)(b << 4);
                return (ushort)(((b << 8) | ((crc >> 8) & 0xFF)) ^ (byte)(b >> 4) ^ (b << 3));
            }
        }
    }
}
