// MavToTelemetryMapper.cs — MAVLink → TelemetryDto (FIXED & TUNED, m/s enforced)
using System;
using System.Buffers.Binary;

namespace arayuz_deneme_1
{
    public static class MavToTelemetryMapper
    {
        // ===== HUD eksen ayarı (kart montajına göre değiştirilebilir) =====
        private static bool SwapRollPitch = false;
        private static int RollSign = +1, PitchSign = +1;

        // ===== Mode kaynağı =====
        private static int _activeSysId = -1;
        private const int AutopilotCompId = 1;
        private static string _lastModeStr = "UNKNOWN";
        private static int _lastMavType = -1, _prevMavType = -1;

        // ===== Heading kaynakları & filtre =====
        private enum HeadingSource { None, Attitude, VfrHud, GpsCog }
        private static HeadingSource _src = HeadingSource.None;

        private static double? _hdgEma;
        private const double HdgAlpha = 0.48;
        private static DateTime _lastHdgTime = DateTime.MinValue;
        private const double HdgMaxDegPerSec = 540.0;

        private const double COG_MIN_GS = 5.0;
        private static DateTime _lastCogTime = DateTime.MinValue;
        private const double COG_HOLD_SEC = 1.0;
        private static DateTime _lastVfrTime = DateTime.MinValue;

        public static double HdgOffsetDeg { get; set; } = 0.0;

        private static double? _yawBiasDeg, _biasEma;
        private const double BiasAlpha = 0.25;
        private static double _lastYawDegAtt = double.NaN;

        public static TelemetryDto UpdateFrom(MavMessage msg, TelemetryDto prev)
        {
            var dto = prev ?? new TelemetryDto();

            switch (msg.MsgId)
            {
                // HEARTBEAT
                case 0:
                    if (msg.Payload.Length >= 9)
                    {
                        uint customMode = BinaryPrimitives.ReadUInt32LittleEndian(msg.Payload.AsSpan(0, 4));
                        byte type = msg.Payload[4];
                        byte baseMode = msg.Payload[6];

                        if (_activeSysId < 0 && msg.CompId == AutopilotCompId)
                            _activeSysId = msg.SysId;

                        if (msg.SysId == _activeSysId && msg.CompId == AutopilotCompId)
                        {
                            _prevMavType = _lastMavType;
                            _lastMavType = type;

                            dto.armed = (baseMode & 0x80) != 0;

                            string modeStr = MapModeByType(_lastMavType, customMode);
                            if (!string.Equals(modeStr, _lastModeStr, StringComparison.Ordinal))
                            {
                                dto.mode = modeStr;
                                _lastModeStr = modeStr;
                            }

                            if (_lastMavType != _prevMavType)
                                ResetHeadingFilter();
                        }
                    }
                    break;

                // ATTITUDE
                case 30:
                    if (msg.Payload.Length >= 16)
                    {
                        DecodeAttitude(msg.Payload, dto);

                        double yawRad = BitConverter.ToSingle(msg.Payload, 12);
                        _lastYawDegAtt = yawRad * 180.0 / Math.PI;

                        if (_src is HeadingSource.None or HeadingSource.Attitude)
                        {
                            double hdgFromYaw = Normalize360((_yawBiasDeg ?? 0.0) + _lastYawDegAtt);
                            dto.headingDeg = SmoothHeading(ApplyOffset(hdgFromYaw));
                            _src = HeadingSource.Attitude;
                        }
                    }
                    break;

                // LOCAL_POSITION_NED
                case 32:
                    if (msg.Payload.Length >= 28)
                    {
                        float vx = BitConverter.ToSingle(msg.Payload, 16);
                        float vy = BitConverter.ToSingle(msg.Payload, 20);
                        double gsp = Math.Sqrt(vx * vx + vy * vy);
                        if (double.IsNaN(dto.groundspeed) || dto.groundspeed <= 0)
                            dto.groundspeed = gsp;
                    }
                    break;

                // GLOBAL_POSITION_INT
                case 33:
                    if (msg.Payload.Length >= 28)
                    {
                        int latE7 = BinaryPrimitives.ReadInt32LittleEndian(msg.Payload.AsSpan(4, 4));
                        int lonE7 = BinaryPrimitives.ReadInt32LittleEndian(msg.Payload.AsSpan(8, 4));
                        int altmm = BinaryPrimitives.ReadInt32LittleEndian(msg.Payload.AsSpan(12, 4));
                        short vxCm = BinaryPrimitives.ReadInt16LittleEndian(msg.Payload.AsSpan(20, 2));
                        short vyCm = BinaryPrimitives.ReadInt16LittleEndian(msg.Payload.AsSpan(22, 2));
                        ushort hdgCd = BinaryPrimitives.ReadUInt16LittleEndian(msg.Payload.AsSpan(26, 2));

                        dto.lat = latE7 / 1e7;
                        dto.lon = lonE7 / 1e7;
                        if (double.IsNaN(dto.altitude) || dto.altitude == 0)
                            dto.altitude = altmm / 1000.0;

                        double gsp = Math.Sqrt((double)vxCm * vxCm + (double)vyCm * vyCm) / 100.0;
                        if (double.IsNaN(dto.groundspeed) || dto.groundspeed <= 0)
                            dto.groundspeed = gsp;

                        bool cogValid = hdgCd != 65535;
                        bool fastEnough = dto.groundspeed >= COG_MIN_GS;
                        bool vfrRecent = (DateTime.UtcNow - _lastVfrTime).TotalSeconds <= 1.5;

                        if (cogValid && fastEnough && !vfrRecent)
                        {
                            double cogDeg = Normalize360(hdgCd / 100.0);
                            dto.headingDeg = SmoothHeading(ApplyOffset(cogDeg));
                            if (_src != HeadingSource.VfrHud) _src = HeadingSource.GpsCog;
                            _lastCogTime = DateTime.UtcNow;
                            CalibrateYawBiasFromReference(dto.headingDeg);
                        }
                        else if (_src == HeadingSource.GpsCog && cogValid)
                        {
                            if (fastEnough || (DateTime.UtcNow - _lastCogTime).TotalSeconds <= COG_HOLD_SEC)
                            {
                                double cogDeg = Normalize360(hdgCd / 100.0);
                                dto.headingDeg = SmoothHeading(ApplyOffset(cogDeg));
                                if (fastEnough) _lastCogTime = DateTime.UtcNow;
                            }
                        }
                    }
                    break;

                // GPS_RAW_INT
                case 24:
                    if (msg.Payload.Length >= 21) dto.sats = msg.Payload[20];
                    break;

                // SYS_STATUS
                case 1:
                    if (msg.Payload.Length >= 16)
                    {
                        ushort vbatmV = BinaryPrimitives.ReadUInt16LittleEndian(msg.Payload.AsSpan(14, 2));
                        dto.battVolt = vbatmV / 1000.0;
                    }
                    break;

                // VFR_HUD
                case 74: // VFR_HUD
                    if (msg.Payload.Length >= 20)
                    {
                        dto.airspeed = BitConverter.ToSingle(msg.Payload, 0);
                        dto.groundspeed = BitConverter.ToSingle(msg.Payload, 4);

                        short hdg16 = BinaryPrimitives.ReadInt16LittleEndian(msg.Payload.AsSpan(8, 2));
                        if (hdg16 >= 0)
                        {
                            double vfrHdg = Normalize360(hdg16);
                            dto.headingDeg = SmoothHeading(ApplyOffset(vfrHdg));
                            _src = HeadingSource.VfrHud;
                            _lastVfrTime = DateTime.UtcNow;
                            CalibrateYawBiasFromReference(vfrHdg);
                        }

                        dto.altitude = BitConverter.ToSingle(msg.Payload, 12);
                    }
                    break;
            }

            // ---- NaN korumaları (m/s enforced) ----
            if (double.IsNaN(dto.groundspeed) || dto.groundspeed < 0)
                dto.groundspeed = 0;

            if (double.IsNaN(dto.airspeed) || dto.airspeed < 0)
                dto.airspeed = 0;

            if (double.IsNaN(dto.altitude))
                dto.altitude = 0;

            if (double.IsNaN(dto.battVolt))
                dto.battVolt = 0;

            if (double.IsNaN(dto.headingDeg))
                dto.headingDeg = _hdgEma ?? 0;

            dto.mode ??= _lastModeStr;
            return dto;
        }

        // ===== Helpers =====
        private static void DecodeAttitude(byte[] payload, TelemetryDto dto)
        {
            float rollRad = BitConverter.ToSingle(payload, 4);
            float pitchRad = BitConverter.ToSingle(payload, 8);
            double rollDeg = rollRad * 180.0 / Math.PI;
            double pitchDeg = pitchRad * 180.0 / Math.PI;

            if (SwapRollPitch) { dto.rollDeg = pitchDeg * RollSign; dto.pitchDeg = rollDeg * PitchSign; }
            else { dto.rollDeg = rollDeg * RollSign; dto.pitchDeg = pitchDeg * PitchSign; }
        }

        private static void ResetHeadingFilter()
        {
            _hdgEma = null;
            _src = HeadingSource.None;
            _lastCogTime = DateTime.MinValue;
            _lastVfrTime = DateTime.MinValue;
            _yawBiasDeg = null; _biasEma = null;
            _lastYawDegAtt = double.NaN;
            _lastHdgTime = DateTime.MinValue;
        }

        private static double ApplyOffset(double hdg) => Normalize360(hdg + HdgOffsetDeg);
        private static double Normalize360(double hdg) { hdg %= 360.0; if (hdg < 0) hdg += 360.0; return hdg; }
        private static double Wrap180(double d) { d = (d + 180.0) % 360.0; if (d < 0) d += 360.0; return d - 180.0; }

        private static double SmoothHeading(double newHdg)
        {
            if (_hdgEma == null) { _hdgEma = newHdg; _lastHdgTime = DateTime.UtcNow; return newHdg; }

            double prev = _hdgEma.Value;
            double diff = Wrap180(newHdg - prev);

            var now = DateTime.UtcNow;
            double dt = Math.Max(1e-3, (now - _lastHdgTime).TotalSeconds);
            _lastHdgTime = now;

            double maxStep = HdgMaxDegPerSec * dt;
            if (diff > maxStep) diff = maxStep;
            if (diff < -maxStep) diff = -maxStep;

            double blended = Normalize360(prev + HdgAlpha * diff);
            _hdgEma = blended;
            return blended;
        }

        private static void CalibrateYawBiasFromReference(double referenceHeadingDeg)
        {
            if (double.IsNaN(_lastYawDegAtt)) return;
            double targetBias = Wrap180(referenceHeadingDeg - _lastYawDegAtt);
            _biasEma = _biasEma is null ? targetBias
                                        : _biasEma + BiasAlpha * Wrap180(targetBias - _biasEma.Value);
            _yawBiasDeg = _biasEma;
        }

        private static string MapModeByType(int mavType, uint customMode)
        {
            bool isCopter =
                   mavType == 2 || mavType == 3 || mavType == 4 ||
                   mavType == 13 || mavType == 14 || mavType == 15 ||
                   mavType == 34;
            return isCopter ? MapArduCopterMode(customMode) : MapArduPlaneMode(customMode);
        }

        private static string MapArduCopterMode(uint m) => m switch
        {
            0 => "STABILIZE",
            1 => "ACRO",
            2 => "ALT_HOLD",
            3 => "AUTO",
            4 => "GUIDED",
            5 => "LOITER",
            6 => "RTL",
            7 => "CIRCLE",
            9 => "LAND",
            11 => "DRIFT",
            13 => "SPORT",
            14 => "FLIP",
            15 => "AUTOTUNE",
            16 => "POSHOLD",
            17 => "BRAKE",
            18 => "THROW",
            19 => "AVOID_ADSB",
            20 => "GUIDED_NOGPS",
            21 => "SMART_RTL",
            22 => "FLOWHOLD",
            23 => "FOLLOW",
            24 => "ZIGZAG",
            25 => "SYSTEMID",
            26 => "AUTOROTATE",
            27 => "AUTO_RTL",
            28 => "TURTLE",
            _ => m.ToString()
        };

        private static string MapArduPlaneMode(uint m) => m switch
        {
            0 => "MANUAL",
            1 => "CIRCLE",
            2 => "STABILIZE",
            3 => "TRAINING",
            4 => "ACRO",
            5 => "FBWA",
            6 => "FBWB",
            7 => "CRUISE",
            8 => "AUTOTUNE",
            10 => "AUTO",
            11 => "RTL",
            12 => "LOITER",
            15 => "GUIDED",
            16 => "INITIALISING",
            17 => "QSTABILIZE",
            18 => "QHOVER",
            19 => "QLOITER",
            20 => "QLAND",
            21 => "QRTL",
            22 => "QAUTOTUNE",
            23 => "QACRO",
            24 => "THERMAL",
            _ => m.ToString()
        };
    }
}
