// TelemetryPanel.xaml.cs (opsiyonel designer dummy)
using System.ComponentModel;
using System.Windows.Controls;

namespace arayuz_deneme_1
{
    public partial class TelemetryPanel : UserControl
    {
        public TelemetryPanel()
        {
            InitializeComponent();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                DataContext = new TelemetryViewModel
                {
                    Mode = "FBWA",
                    Armed = true,
                    Lat = 40.195,
                    Lon = 29.060,
                    Alt = 312.4,
                    Airspeed = 22.1,
                    GroundSpeed = 20.8,
                    HeadingDeg = 182,
                    Pitch = 1.6,
                    Roll = -4.2,
                    Sats = 12,
                    BattVolt = 15.7
                };
            }
        }
    }
}
