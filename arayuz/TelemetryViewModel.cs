using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace arayuz_deneme_1
{
    public class TelemetryViewModel : INotifyPropertyChanged, IDisposable
    {
        double _pitch, _roll, _headingDeg;
        double _airspeed, _groundSpeed, _alt;
        double _lat, _lon, _battVolt;
        int _sats;
        bool _armed;
        string _mode = "UNKNOWN";

        private readonly Action<TelemetryDto> _onPkt;
        private bool _disposed;

        public TelemetryViewModel()
        {
            _onPkt = dto => SafePostToUi(() => UpdateFromDto(dto));
            TelemetryHub.Instance.OnPacket += _onPkt;

            if (Application.Current != null)
                Application.Current.Exit += OnAppExit;
        }

        private void OnAppExit(object? s, ExitEventArgs e) => Dispose();

        private void SafePostToUi(Action act)
        {
            try
            {
                var disp = Application.Current?.Dispatcher ?? App.Current?.Dispatcher;
                if (disp == null || disp.HasShutdownStarted || disp.HasShutdownFinished) return;

                if (disp.CheckAccess()) act();
                else disp.BeginInvoke(act);
            }
            catch { }
        }

        private void UpdateFromDto(TelemetryDto dto)
        {
            Pitch = dto.pitchDeg;
            Roll = dto.rollDeg;
            HeadingDeg = dto.headingDeg;
            Alt = dto.altitude;
            Airspeed = dto.airspeed;
            GroundSpeed = dto.groundspeed;
            Lat = dto.lat;
            Lon = dto.lon;
            Mode = dto.mode;
            Armed = dto.armed;
            BattVolt = dto.battVolt;
            Sats = dto.sats;
        }

        // === Properties ===
        public double Pitch { get => _pitch; set { _pitch = value; OnChanged(); } }
        public double Roll { get => _roll; set { _roll = value; OnChanged(); } }
        public double HeadingDeg { get => _headingDeg; set { _headingDeg = value; OnChanged(); } }

        public double Airspeed { get => _airspeed; set { _airspeed = value; OnChanged(); } }
        public double GroundSpeed { get => _groundSpeed; set { _groundSpeed = value; OnChanged(); } }
        public double Alt { get => _alt; set { _alt = value; OnChanged(); } }

        public double Lat { get => _lat; set { _lat = value; OnChanged(); } }
        public double Lon { get => _lon; set { _lon = value; OnChanged(); } }

        public int Sats { get => _sats; set { _sats = value; OnChanged(); } }
        public double BattVolt { get => _battVolt; set { _battVolt = value; OnChanged(); } }

        public bool Armed { get => _armed; set { _armed = value; OnChanged(); } }
        public string Mode { get => _mode; set { _mode = value; OnChanged(); } }

        // === INotifyPropertyChanged ===
        public event PropertyChangedEventHandler? PropertyChanged;
        void OnChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        // === Dispose ===
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { TelemetryHub.Instance.OnPacket -= _onPkt; } catch { }
            if (Application.Current != null) Application.Current.Exit -= OnAppExit;
            try { TelemetryHub.Instance?.Stop(); } catch { }
        }
    }
}
