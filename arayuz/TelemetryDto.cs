namespace arayuz_deneme_1
{
    public class TelemetryDto
    {
        public double pitchDeg { get; set; } = double.NaN;
        public double rollDeg { get; set; } = double.NaN;
        public double headingDeg { get; set; } = double.NaN;

        public double airspeed { get; set; } = double.NaN;
        public double groundspeed { get; set; } = double.NaN;

        public double altitude { get; set; } = double.NaN;

        public double lat { get; set; } = double.NaN;
        public double lon { get; set; } = double.NaN;

        public int sats { get; set; } = 0;

        public double battVolt { get; set; } = double.NaN;

        public bool armed { get; set; }
        public string mode { get; set; } = "";
    }
}
