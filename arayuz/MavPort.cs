using System;

namespace arayuz_deneme_1
{
    /// Tek noktadan MAVLink v2 (imzasız) komut gönderen basit verici.
    public static class MavPort
    {
        private static Action<byte[]>? _write;   // MainWindow serial.write
        private static byte _seq;
        private static byte _targetSys = 1, _targetComp = 1;

        // GÖNDEREN (GCS) kimliği — ArduPilot ile uyumlu varsayılanlar
        private const byte SENDER_SYSID = 255;
        private const byte SENDER_COMPID = 190;

        private const byte MAV_V2 = 0xFD;

        // CRC_EXTRA
        private const byte CRC_SET_MODE = 89;   // msg 11
        private const byte CRC_COMMAND_LONG = 152;  // msg 76

        public static void Init(Action<byte[]> writer)
        {
            _write = writer;
            _seq = 0;
        }

        // --------- Public API (butonların çağırdığı) ---------
        public static void Arm(bool arm, bool force = false)
            => CommandLong(400, arm ? 1f : 0f, force ? 21196f : 0f); // MAV_CMD_COMPONENT_ARM_DISARM

        public static void SetMode(uint customMode, byte baseMode = 1 << 7 /* MAV_MODE_FLAG_CUSTOM_MODE_ENABLED */)
        {
            // SET_MODE payload: target_system (u8), base_mode (u8), custom_mode (u32 LE)
            var payload = new byte[6];
            payload[0] = _targetSys;
            payload[1] = baseMode;
            BitConverter.GetBytes(customMode).CopyTo(payload, 2);
            SendFrame(11u, payload, CRC_SET_MODE);
        }

        public static void RTL() => SetMode(PlaneModes.Map["RTL"]);
        public static void Guided() => SetMode(PlaneModes.Map["GUIDED"]);
        public static void Auto() => SetMode(PlaneModes.Map["AUTO"]);
        public static void Loiter() => SetMode(PlaneModes.Map["LOITER"]);
        public static void Manual() => SetMode(PlaneModes.Map["MANUAL"]);
        public static void FBWA() => SetMode(PlaneModes.Map["FBWA"]);
        public static void Cruise() => SetMode(PlaneModes.Map["CRUISE"]);

        public static void FenceEnable(bool enable)
            => CommandLong(207, enable ? 1f : 0f); // MAV_CMD_DO_FENCE_ENABLE

        public static void TakeoffGuided(float altMeters)
        {
            Guided();
            Arm(true);
            // MAV_CMD_NAV_TAKEOFF (22) – Plane’de çoğunlukla p7=alt
            CommandLong(22, p7: altMeters);
        }

        public static void LandGuided()
        {
            Guided();
            // MAV_CMD_NAV_LAND (21) – basit LAND
            CommandLong(21);
        }

        public static void Kill() => CommandLong(185, 1f); // MAV_CMD_DO_FLIGHTTERMINATION
        public static void Reboot() => CommandLong(246, 1f); // MAV_CMD_PREFLIGHT_REBOOT_SHUTDOWN

        public static void CommandLong(ushort command,
                                       float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0,
                                       float p5 = 0, float p6 = 0, float p7 = 0, byte confirmation = 0)
        {
            // COMMAND_LONG payload: 7*float + uint16 command + target_sys + target_comp + confirmation
            var payload = new byte[33];
            int o = 0;
            void Wf(float v) { BitConverter.GetBytes(v).CopyTo(payload, o); o += 4; }
            Wf(p1); Wf(p2); Wf(p3); Wf(p4); Wf(p5); Wf(p6); Wf(p7);
            var cmd = BitConverter.GetBytes(command);
            payload[o++] = cmd[0]; payload[o++] = cmd[1];
            payload[o++] = _targetSys;
            payload[o++] = _targetComp;
            payload[o++] = confirmation;

            SendFrame(76u, payload, CRC_COMMAND_LONG);
        }

        // --------- Frame Builder ---------
        private static void SendFrame(uint msgId, byte[] payload, byte extraCrc)
        {
            if (_write == null) return;

            byte len = (byte)payload.Length;
            var frame = new byte[10 + len + 2];

            frame[0] = MAV_V2;
            frame[1] = len;
            frame[2] = 0x00; // incompat
            frame[3] = 0x00; // compat
            frame[4] = _seq++;           // seq
            frame[5] = SENDER_SYSID;     // <<< GÖNDEREN sysid (255)
            frame[6] = SENDER_COMPID;    // <<< GÖNDEREN compid (190)
            frame[7] = (byte)(msgId & 0xFF);
            frame[8] = (byte)((msgId >> 8) & 0xFF);
            frame[9] = (byte)((msgId >> 16) & 0xFF);

            Buffer.BlockCopy(payload, 0, frame, 10, len);

            ushort crc = X25(frame, 1, 9 + len);
            crc = Acc(crc, extraCrc);
            frame[10 + len] = (byte)(crc & 0xFF);
            frame[11 + len] = (byte)((crc >> 8) & 0xFF);

            _write(frame);
        }

        private static ushort X25(byte[] buf, int off, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++) crc = Acc(crc, buf[off + i]);
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
