using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace arayuz_deneme_1
{
    public partial class HudControl : UserControl
    {
        private const double PixelsPerPitchDeg = 6.0;

        // === FONT SCALE (daha da büyütüldü) ===
        private const double RollLabelFont = 22;     // 14 → 22
        private const double PitchLabelFont = 26;    // 16 → 26
        private const double HeadingLabelFont = 26;  // 16 → 26

        public HudControl()
        {
            InitializeComponent();
            BuildStaticRollTicks();
            BuildHeadingTapeMarks();
            BuildPitchLadder();
            BuildRollDynamicScale();
        }

        private void BuildRollDynamicScale()
        {
            RollDynamicScale.Children.Clear();

            Point center = new(400, 300);
            double radius = 220;

            for (int deg = -90; deg <= 90; deg += 10)
            {
                double rad = (Math.PI / 180.0) * (deg - 90);
                var dir = new Vector(Math.Cos(rad), Math.Sin(rad));

                double tickLen = (deg % 30 == 0) ? 18 : 10;
                Point pOuter = new(center.X + dir.X * radius, center.Y + dir.Y * radius);
                Point pInner = new(center.X + dir.X * (radius - tickLen), center.Y + dir.Y * (radius - tickLen));

                var tick = new Line
                {
                    X1 = pInner.X,
                    Y1 = pInner.Y,
                    X2 = pOuter.X,
                    Y2 = pOuter.Y,
                    Stroke = Brushes.Lime,
                    StrokeThickness = 2
                };
                RollDynamicScale.Children.Add(tick);

                if (deg % 30 == 0)
                {
                    double labelR = radius + 28; // biraz dışarı
                    Point lp = new(center.X + dir.X * labelR, center.Y + dir.Y * labelR);

                    var tb = new TextBlock
                    {
                        Text = $"{Math.Abs(deg)}°",
                        Foreground = Brushes.Lime,
                        FontSize = RollLabelFont,
                        FontWeight = FontWeights.SemiBold,
                        Opacity = 0.9
                    };

                    // büyük font için ortalamayı biraz düzelt
                    Canvas.SetLeft(tb, lp.X - 16);
                    Canvas.SetTop(tb, lp.Y - 14);
                    RollDynamicScale.Children.Add(tb);
                }
            }
        }

        #region Dependency Properties
        public double PitchDeg
        {
            get => (double)GetValue(PitchDegProperty);
            set => SetValue(PitchDegProperty, value);
        }
        public static readonly DependencyProperty PitchDegProperty =
            DependencyProperty.Register(nameof(PitchDeg), typeof(double), typeof(HudControl),
                new PropertyMetadata(0.0, OnAttitudeChanged));

        public double RollDeg
        {
            get => (double)GetValue(RollDegProperty);
            set => SetValue(RollDegProperty, value);
        }
        public static readonly DependencyProperty RollDegProperty =
            DependencyProperty.Register(nameof(RollDeg), typeof(double), typeof(HudControl),
                new PropertyMetadata(0.0, OnAttitudeChanged));

        public double HeadingDeg
        {
            get => (double)GetValue(HeadingDegProperty);
            set => SetValue(HeadingDegProperty, value);
        }
        public static readonly DependencyProperty HeadingDegProperty =
            DependencyProperty.Register(nameof(HeadingDeg), typeof(double), typeof(HudControl),
                new PropertyMetadata(0.0, OnHeadingChanged));

        public double Airspeed
        {
            get => (double)GetValue(AirspeedProperty);
            set => SetValue(AirspeedProperty, value);
        }
        public static readonly DependencyProperty AirspeedProperty =
            DependencyProperty.Register(nameof(Airspeed), typeof(double), typeof(HudControl),
                new PropertyMetadata(0.0, OnNumericChanged));

        public double Altitude
        {
            get => (double)GetValue(AltitudeProperty);
            set => SetValue(AltitudeProperty, value);
        }
        public static readonly DependencyProperty AltitudeProperty =
            DependencyProperty.Register(nameof(Altitude), typeof(double), typeof(HudControl),
                new PropertyMetadata(0.0, OnNumericChanged));

        public string Mode
        {
            get => (string)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }
        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register(nameof(Mode), typeof(string), typeof(HudControl),
                new PropertyMetadata("STABILIZE", OnTextChanged));

        public bool IsArmed
        {
            get => (bool)GetValue(IsArmedProperty);
            set => SetValue(IsArmedProperty, value);
        }
        public static readonly DependencyProperty IsArmedProperty =
            DependencyProperty.Register(nameof(IsArmed), typeof(bool), typeof(HudControl),
                new PropertyMetadata(false, OnTextChanged));
        #endregion

        private static void OnAttitudeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var hud = (HudControl)d;

            // Dünya / horizon için roll ters işaretli
            if (hud.RollRotate != null)
                hud.RollRotate.Angle = -hud.RollDeg;

            // Pitch için PFD davranışı: ters
            if (hud.PitchTranslate != null)
                hud.PitchTranslate.Y = -hud.PitchDeg * PixelsPerPitchDeg;

            // Roll skala ters
            if (hud.RollScaleRotate != null)
                hud.RollScaleRotate.Angle = -hud.RollDeg;
        }

        private static void OnHeadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var hud = (HudControl)d;
            hud.UpdateHeadingTape();
        }

        private static void OnNumericChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var hud = (HudControl)d;

            if (hud.TxtSpeed != null)
                hud.TxtSpeed.Text = Math.Round(hud.Airspeed).ToString();

            if (hud.TxtAlt != null)
                hud.TxtAlt.Text = Math.Round(hud.Altitude).ToString();
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var hud = (HudControl)d;

            if (hud.TxtMode != null)
                hud.TxtMode.Text = "MODE: " + (hud.Mode ?? "");

            if (hud.TxtArm != null)
                hud.TxtArm.Text = hud.IsArmed ? "ARMED" : "DISARMED";
        }

        // ===== Pitch Ladder =====
        private void BuildPitchLadder()
        {
            PitchLadderHost.Children.Clear();
            for (int deg = -30; deg <= 30; deg += 5)
            {
                if (deg == 0) continue;
                var isMajor = deg % 10 == 0;

                double y = -deg * PixelsPerPitchDeg;
                double lineLen = isMajor ? 220 : 120;
                double leftX = -lineLen / 2.0;
                double rightX = +lineLen / 2.0;

                var line = new Line
                {
                    X1 = leftX,
                    Y1 = y,
                    X2 = rightX,
                    Y2 = y,
                    Stroke = Brushes.Lime,
                    StrokeThickness = isMajor ? 2 : 1
                };
                PitchLadderHost.Children.Add(line);

                if (isMajor)
                {
                    PitchLadderHost.Children.Add(MakePitchLabel(deg.ToString(), leftX - 60, y - 16));
                    PitchLadderHost.Children.Add(MakePitchLabel(deg.ToString(), rightX + 14, y - 16));
                }
            }
        }

        private UIElement MakePitchLabel(string text, double x, double y)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = Brushes.Lime,
                FontSize = PitchLabelFont,
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }

        private void BuildStaticRollTicks()
        {
            RollTicksHost.Children.Clear();
            Point center = new(400, 340);
            double radius = 220;

            for (int deg = -60; deg <= 60; deg += 10)
            {
                double rad = (Math.PI / 180.0) * (deg - 90);
                var dir = new Vector(Math.Cos(rad), Math.Sin(rad));

                double tickLen = (deg % 30 == 0) ? 18 : 10;
                Point pOuter = new(center.X + dir.X * radius, center.Y + dir.Y * radius);
                Point pInner = new(center.X + dir.X * (radius - tickLen), center.Y + dir.Y * (radius - tickLen));

                var tick = new Line
                {
                    X1 = pInner.X,
                    Y1 = pInner.Y,
                    X2 = pOuter.X,
                    Y2 = pOuter.Y,
                    Stroke = Brushes.Lime,
                    StrokeThickness = 2
                };
                RollTicksHost.Children.Add(tick);
            }
        }

        private const double PixelsPerHeadingDeg = 6.0;

        private void BuildHeadingTapeMarks()
        {
            HeadingTape.Children.Clear();
            for (int round = -2; round <= 3; round++)
            {
                for (int hdg = 0; hdg < 360; hdg += 5)
                {
                    double x = (hdg + round * 360) * PixelsPerHeadingDeg;
                    bool major = hdg % 10 == 0;

                    var tick = new Rectangle
                    {
                        Width = 2,
                        Height = major ? 20 : 12,
                        Fill = Brushes.Lime
                    };
                    Canvas.SetLeft(tick, x);
                    Canvas.SetTop(tick, major ? 0 : 8);
                    HeadingTape.Children.Add(tick);

                    if (major)
                    {
                        var tb = new TextBlock
                        {
                            Foreground = Brushes.Lime,
                            FontSize = HeadingLabelFont,
                            FontWeight = FontWeights.SemiBold,
                            Text = HdgText(hdg)
                        };
                        Canvas.SetLeft(tb, x - 14);
                        Canvas.SetTop(tb, 22);
                        HeadingTape.Children.Add(tb);
                    }
                }
            }

            // Not: HeadingTape.Width = "360*6*PixelsPerHeadingDeg" yanlış büyüyor, doğru olan:
            HeadingTape.Width = (360 * PixelsPerHeadingDeg);
        }

        private string HdgText(int hdg)
        {
            return hdg switch
            {
                0 => "N",
                90 => "E",
                180 => "S",
                270 => "W",
                _ => hdg.ToString()
            };
        }

        private void UpdateHeadingTape()
        {
            double centerX = 400.0;
            double hdg = HeadingDeg % 360.0;
            if (hdg < 0) hdg += 360.0;

            double offset = -hdg * PixelsPerHeadingDeg + centerX;
            HeadingTapeTranslate.X = offset;
        }

        // ===== Dış API =====
        public void UpdateHud(double pitchDeg, double rollDeg, double headingDeg, double airspeed, double altitude, string mode = null, bool? armed = null)
        {
            PitchDeg = pitchDeg;
            RollDeg = rollDeg;
            HeadingDeg = headingDeg;
            Airspeed = airspeed;
            Altitude = altitude;
            if (mode != null) Mode = mode;
            if (armed.HasValue) IsArmed = armed.Value;
        }

        public void UpdateFromDto(TelemetryDto dto)
        {
            UpdateHud(dto.pitchDeg, dto.rollDeg, dto.headingDeg,
                      dto.airspeed, dto.altitude,
                      dto.mode, dto.armed);
        }
    }
}
