// PlanWaypointPanel.xaml.cs — Uydu haritalı, sadeleştirilmiş rota çizimi + WPL110 model
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;           // ← EKLENDİ (async için)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

using GMap.NET;
using GMap.NET.MapProviders;
using WP = GMap.NET.WindowsPresentation; // WPF tipi alias

namespace arayuz_deneme_1
{
    public partial class PlanWaypointPanel : UserControl
    {
        // === Katmanlar (overlay'siz) ===
        private readonly Dictionary<string, List<WP.GMapMarker>> _layers = new()
        {
            ["wp"] = new List<WP.GMapMarker>(),   // waypoint pinleri
            ["route"] = new List<WP.GMapMarker>(),// rota (tek GMapRoute olacak)
            ["plane"] = new List<WP.GMapMarker>() // uçak/heading (istersen kullan)
        };

        // Açık katmanlar
        private readonly HashSet<string> _enabled = new() { "wp", "route", "plane" };

        private PlaneLayer? _plane;         // projende var
        private bool _subscribed;
        private bool _inited;
        private bool _uiEventsHooked;
        private bool _followAircraft = false;

        private double _lastLat = 40.1955, _lastLon = 29.0612; // telemetriden gelen en son konum

        private readonly PlanVM _vm = new();

        public PlanWaypointPanel()
        {
            InitializeComponent();
            DataContext = _vm;
            Loaded += Plan_Loaded;
        }

        private async void Plan_Loaded(object? sender, RoutedEventArgs e)
        {
            if (!_inited)
            {
                InitMap();                  // --- HARİTAYI BURADA UYDU YAPTIK ---
                HookToolbarButtons();
                WireWaypointChangeHandlers();
                RebuildAllLayers();         // başlangıç katman kurulum
                _inited = true;
            }

            // UI eventleri (zoom & mouse koordinatı)
            if (!_uiEventsHooked)
            {
                GMap.OnMapZoomChanged += GMap_OnMapZoomChanged;
                GMap.MouseMove += GMap_MouseMove;
                GMap.MouseLeave += GMap_MouseLeave;
                UpdateZoomLabel(); // initial
                _uiEventsHooked = true;
            }

            // Tek merkez telemetry aboneliği
            await TelemetryHub.Instance.EnsureStartedAsync();
            if (!_subscribed)
            {
                TelemetryHub.Instance.OnPacket += OnTelemetry;
                _subscribed = true;
            }
        }

        // ================== MAP ==================
        private void InitMap()
        {
            // Cache klasörü (istersen değiştir)
            GMap.CacheLocation = @"C:\GMapCache";
            GMaps.Instance.Mode = AccessMode.ServerAndCache;

            // === SADE UYDU HARİTA ===
            // Esri World Imagery (uydu) — MbtilesHttpProvider'ı HTTP tile provider gibi kullanıyoruz.
            MbtilesHttpProvider.SetTemplate(
                "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
            );
            GMap.MapProvider = MbtilesHttpProvider.Instance;

            GMap.MinZoom = 3;
            GMap.MaxZoom = 25;
            GMap.Zoom = 15;
            GMap.ShowCenter = false;
            GMap.Position = new PointLatLng(_lastLat, _lastLon);

            // Uçak katmanı (projendeki sınıf)
            _plane = new PlaneLayer(GMap);

            // Haritaya tıklayınca WP ekle (WPL110 varsayılanlarıyla)
            GMap.MouseLeftButtonUp += (s, e) =>
            {
                var p = e.GetPosition(GMap);
                var ll = GMap.FromLocalToLatLng((int)p.X, (int)p.Y);
                _vm.AddWaypointAtEnd(new WaypointItem
                {
                    // WPL110 alanları
                    Index = _vm.Waypoints.Count, // 0..N normalize ediliyor
                    Current = 0,
                    Frame = 3,            // GLOBAL_RELATIVE_ALT
                    Command = 16,         // NAV_WAYPOINT
                    Param1 = 0,
                    Param2 = 0,
                    Param3 = 0,
                    Param4 = 0,
                    X = Math.Round(ll.Lat, 8),
                    Y = Math.Round(ll.Lng, 8),
                    Z = 20,
                    AutoContinue = 1
                });
                RebuildAllLayers();
            };
        }

        // ============== TELEMETRY ==============
        private void OnTelemetry(TelemetryDto dto)
        {
            Dispatcher.Invoke(() =>
            {
                // Son konumu sakla
                _lastLat = double.IsNaN(dto.lat) ? _lastLat : dto.lat;
                _lastLon = double.IsNaN(dto.lon) ? _lastLon : dto.lon;

                _plane?.Update(_lastLat, _lastLon, dto.headingDeg);

                if (_followAircraft)
                    GMap.Position = new PointLatLng(_lastLat, _lastLon);
            });
        }

        // ====== Overlay'siz katman yönetimi ======
        private void EnableLayer(string name, bool enable)
        {
            if (enable)
            {
                if (_enabled.Add(name))
                    AttachLayerToMap(name);
            }
            else
            {
                if (_enabled.Remove(name))
                    DetachLayerFromMap(name);
            }
        }

        private void AttachLayerToMap(string name)
        {
            if (!_layers.TryGetValue(name, out var list)) return;

            foreach (var m in list)
            {
                if (!GMap.Markers.Contains(m))
                    GMap.Markers.Add(m);

                // Rota ise: Path stroke ayarını yap
                if (m is WP.GMapRoute r)
                {
                    if (r.Shape is System.Windows.Shapes.Path path)
                    {
                        path.Stroke = Brushes.DeepSkyBlue;
                        path.StrokeThickness = 2.0;
                    }
                }
            }
        }

        private void DetachLayerFromMap(string name)
        {
            if (!_layers.TryGetValue(name, out var list)) return;
            foreach (var m in list) GMap.Markers.Remove(m);
        }

        private void ClearLayer(string name)
        {
            if (!_layers.TryGetValue(name, out var list)) return;
            foreach (var m in list) GMap.Markers.Remove(m);
            list.Clear();
        }

        private void RebuildAllLayers()
        {
            BuildWpLayer();
            BuildRouteLayer();
            BuildPlaneLayer(); // plane marker’ını PlaneLayer yönetiyor; gerekirse ek sınıf ekleyebilirsin

            // Açık olanları haritaya tak
            foreach (var key in _layers.Keys)
            {
                if (_enabled.Contains(key)) AttachLayerToMap(key);
                else DetachLayerFromMap(key);
            }
        }

        private void BuildWpLayer()
        {
            ClearLayer("wp");
            foreach (var wp in _vm.Waypoints)
            {
                if (!wp.HasValidXY) continue;

                var marker = new WP.GMapMarker(new PointLatLng(wp.X, wp.Y))
                {
                    Shape = MakeWpShape(wp.Index, wp == _vm.SelectedWaypoint),
                    ZIndex = 10
                };
                _layers["wp"].Add(marker);
            }
        }

        private void BuildRouteLayer()
        {
            ClearLayer("route");

            var pts = _vm.Waypoints.Where(w => w.HasValidXY)
                                   .Select(w => new PointLatLng(w.X, w.Y))
                                   .ToList();
            if (pts.Count >= 2)
            {
                // GMapRoute'u sade bırakıyoruz; shape/çizim işini GMap.NET kendi yapıyor.
                var route = new WP.GMapRoute(pts)
                {
                    ZIndex = 5
                };

                _layers["route"].Add(route);
            }
        }

        private void BuildPlaneLayer()
        {
            // Şimdilik PlaneLayer haritaya kendi çizimini yapıyor diye boş.
            ClearLayer("plane");
            // Eğer kendi markerını da istiyorsan buraya ekleyebilirsin.
        }

        private FrameworkElement MakeWpShape(int index, bool selected)
        {
            var back = selected ? Brushes.OrangeRed
                                : (Brush)new SolidColorBrush(Color.FromRgb(0x4F, 0x8E, 0xF7));
            return new Border
            {
                Background = back,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 4, 8, 4),
                Child = new TextBlock
                {
                    Text = index.ToString(),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12
                }
            };
        }

        // ====== Yardımcı: Buton etiketi sadeleştir (.waypoints parantezlerini at) ======
        private static string SimplifyLabel(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            int i = s.IndexOf('(');
            if (i >= 0) s = s.Substring(0, i);
            return s.Trim();
        }

        // ====== Toolbar (x:Name vermeden bağlama) ======
        private void HookToolbarButtons()
        {
            foreach (var btn in FindVisualChildren<Button>(this))
            {
                var label = SimplifyLabel(ExtractButtonLabel(btn));
                if (string.IsNullOrWhiteSpace(label)) continue;

                switch (label)
                {
                    // HARİTA
                    case "Yakınlaş":
                        btn.Click += (_, __) => GMap.Zoom = Math.Min(GMap.Zoom + 1, GMap.MaxZoom);
                        break;
                    case "Uzaklaş":
                        btn.Click += (_, __) => GMap.Zoom = Math.Max(GMap.Zoom - 1, GMap.MinZoom);
                        break;
                    case "Uçağı Merkezle":
                        btn.Click += (_, __) => GMap.Position = new PointLatLng(_lastLat, _lastLon);
                        break;

                    // TABLO / WP
                    case "Ekle":
                        btn.Click += (_, __) => { _vm.AddWaypointAtEnd(new WaypointItem()); RebuildAllLayers(); };
                        break;
                    case "Araya Ekle":
                        btn.Click += (_, __) => { _vm.InsertAfterSelected(); RebuildAllLayers(); };
                        break;
                    case "Sil":
                        btn.Click += (_, __) => { _vm.DeleteSelected(); RebuildAllLayers(); };
                        break;
                    case "Yukarı":
                        btn.Click += (_, __) => { _vm.MoveSelected(-1); RebuildAllLayers(); };
                        break;
                    case "Aşağı":
                        btn.Click += (_, __) => { _vm.MoveSelected(1); RebuildAllLayers(); };
                        break;
                    case "Temizle":
                        btn.Click += (_, __) => { _vm.ClearAll(); RebuildAllLayers(); };
                        break;

                    // DOSYA (etiket varyasyonlarına dayanıklı)
                    case "İçe Aktar":
                    case string l when l.StartsWith("İçe Aktar", StringComparison.OrdinalIgnoreCase):
                        btn.Click += async (_, __) => { await ImportAsync(); RebuildAllLayers(); };
                        break;

                    case "Dışa Aktar":
                    case string l when l.StartsWith("Dışa Aktar", StringComparison.OrdinalIgnoreCase):
                        btn.Click += async (_, __) => { await ExportAsync(); };
                        break;

                    // KONTROL
                    case "Doğrula":
                        btn.Click += (_, __) =>
                        {
                            var report = _vm.Validate();
                            MessageBox.Show(report, "Görev Doğrulama",
                                MessageBoxButton.OK,
                                report.Contains("HATA") ? MessageBoxImage.Warning : MessageBoxImage.Information);
                        };
                        break;
                }
            }
        }

        private static string ExtractButtonLabel(Button btn)
        {
            if (btn.Content is StackPanel sp)
                foreach (var child in sp.Children)
                    if (child is TextBlock tb && (tb.Text ?? "").Any(char.IsLetter))
                        return tb.Text!.Trim();
            return (btn.Content as string) ?? "";
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) yield break;
            var n = VisualTreeHelper.GetChildrenCount(depObj);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(depObj, i);
                if (c is T t) yield return t;
                foreach (var cc in FindVisualChildren<T>(c)) yield return cc;
            }
        }

        // ====== WP <-> MAP Sync (katman rebuild tetikleyici) ======
        private void WireWaypointChangeHandlers()
        {
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PlanVM.SelectedWaypoint) or
                                      nameof(PlanVM.TotalDistanceKm) or
                                      nameof(PlanVM.EstimatedTimeMin))
                    RebuildAllLayers();
            };

            _vm.Waypoints.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (WaypointItem it in e.NewItems)
                        it.PropertyChanged += (_, __) => RebuildAllLayers();

                RebuildAllLayers();
            };
        }

        // ====== Import/Export (QGC WPL 110) ======
        private async Task ImportAsync()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "QGroundControl Waypoints (*.waypoints)|*.waypoints|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var text = await File.ReadAllTextAsync(dlg.FileName, Encoding.UTF8);
                    _vm.LoadFromWpl110(text);
                }
                catch (Exception ex) { MessageBox.Show("İçe aktarım hatası: " + ex.Message); }
            }
        }

        private async Task ExportAsync()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "QGroundControl Waypoints (*.waypoints)|*.waypoints",
                FileName = "mission.waypoints"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var text = _vm.ToWpl110Text();
                    await File.WriteAllTextAsync(dlg.FileName, text, Encoding.UTF8);
                }
                catch (Exception ex) { MessageBox.Show("Dışa aktarım hatası: " + ex.Message); }
            }
        }

        // ====== ZOOM & MOUSE LAT/LON ======
        private void GMap_OnMapZoomChanged()
        {
            Dispatcher.Invoke(UpdateZoomLabel);
        }

        private void UpdateZoomLabel()
        {
            var z = (int)Math.Round(GMap.Zoom);
            if (TbZoom != null)
                TbZoom.Text = $"Zoom: {z}";
        }

        private void GMap_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(GMap);
            PointLatLng ll = GMap.FromLocalToLatLng((int)p.X, (int)p.Y);
            if (TbLatLon != null)
                TbLatLon.Text = $"Lat/Lon: {ll.Lat.ToString("F6", CultureInfo.InvariantCulture)} / {ll.Lng.ToString("F6", CultureInfo.InvariantCulture)}";
        }

        private void GMap_MouseLeave(object sender, MouseEventArgs e)
        {
            if (TbLatLon != null)
                TbLatLon.Text = "Lat/Lon: — / —";
        }
    }

    // ================= VM / Model (QGC WPL 110) =================
    public class PlanVM : INotifyPropertyChanged
    {
        public ObservableCollection<WaypointItem> Waypoints { get; } = new();

        private WaypointItem? _selected;
        public WaypointItem? SelectedWaypoint
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; OnPropertyChanged(nameof(SelectedWaypoint)); } }
        }

        private double _totalKm;
        public double TotalDistanceKm
        {
            get => _totalKm;
            private set { if (Math.Abs(_totalKm - value) > 1e-6) { _totalKm = value; OnPropertyChanged(nameof(TotalDistanceKm)); } }
        }

        private double _etaMin;
        public double EstimatedTimeMin
        {
            get => _etaMin;
            private set { if (Math.Abs(_etaMin - value) > 1e-6) { _etaMin = value; OnPropertyChanged(nameof(EstimatedTimeMin)); } }
        }

        public PlanVM() => Waypoints.CollectionChanged += (_, __) => Recompute();

        public void AddWaypointAtEnd(WaypointItem? item)
        {
            var it = item ?? new WaypointItem();
            Waypoints.Add(it);
            RenumberZeroBased();
            SelectedWaypoint = it;
            Recompute();
        }

        public void InsertAfterSelected()
        {
            int idx = _selected != null ? Waypoints.IndexOf(_selected) + 1 : Waypoints.Count;
            idx = Clamp(idx, 0, Waypoints.Count);
            var ins = new WaypointItem();
            Waypoints.Insert(idx, ins);
            RenumberZeroBased();
            SelectedWaypoint = ins;
            Recompute();
        }

        public void DeleteSelected()
        {
            if (_selected == null) return;
            var i = Waypoints.IndexOf(_selected);
            if (i >= 0) Waypoints.RemoveAt(i);
            RenumberZeroBased();
            SelectedWaypoint = Waypoints.Count > 0 ? Waypoints[Math.Min(i, Waypoints.Count - 1)] : null;
            Recompute();
        }

        public void MoveSelected(int delta)
        {
            if (_selected == null) return;
            var i = Waypoints.IndexOf(_selected);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= Waypoints.Count) return;
            Waypoints.Move(i, j);
            RenumberZeroBased();
            SelectedWaypoint = Waypoints[j];
            Recompute();
        }

        public void ClearAll()
        {
            Waypoints.Clear();
            SelectedWaypoint = null;
            Recompute();
        }

        public void Load(IEnumerable<WaypointItem> items)
        {
            Waypoints.Clear();
            foreach (var it in items) Waypoints.Add(it);
            RenumberZeroBased();
            SelectedWaypoint = Waypoints.FirstOrDefault();
            Recompute();
        }

        // ---- WPL110 İçe/Dışa Aktarma ----
        public void LoadFromWpl110(string text)
        {
            Waypoints.Clear();

            var lines = text.Replace("\r\n", "\n").Split('\n')
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .ToList();

            if (lines.Count == 0 || !lines[0].Trim().Equals("QGC WPL 110", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Başlık yok/yanlış. Beklenen: 'QGC WPL 110'");

            for (int i = 1; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#")) continue;

                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 12) continue;

                try
                {
                    var w = new WaypointItem
                    {
                        Index = ParseI(parts[0]),
                        Current = ParseI(parts[1]),
                        Frame = ParseI(parts[2]),
                        Command = ParseI(parts[3]),
                        Param1 = ParseD(parts[4]),
                        Param2 = ParseD(parts[5]),
                        Param3 = ParseD(parts[6]),
                        Param4 = ParseD(parts[7]),
                        X = ParseD(parts[8]),
                        Y = ParseD(parts[9]),
                        Z = ParseD(parts[10]),
                        AutoContinue = ParseI(parts[11])
                    };
                    Waypoints.Add(w);
                }
                catch { /* satırı atla */ }
            }

            RenumberZeroBased(); // 0..N
            SelectedWaypoint = Waypoints.FirstOrDefault();
            Recompute();
        }

        public string ToWpl110Text()
        {
            var sb = new StringBuilder();
            sb.AppendLine("QGC WPL 110");
            for (int i = 0; i < Waypoints.Count; i++)
            {
                var w = Waypoints[i];
                sb.AppendJoin('\t', new[]
                {
                    i.ToString(CultureInfo.InvariantCulture),
                    w.Current.ToString(CultureInfo.InvariantCulture),
                    w.Frame.ToString(CultureInfo.InvariantCulture),
                    w.Command.ToString(CultureInfo.InvariantCulture),
                    F(w.Param1), F(w.Param2), F(w.Param3), F(w.Param4),
                    F(w.X), F(w.Y), F(w.Z),
                    w.AutoContinue.ToString(CultureInfo.InvariantCulture)
                });
                sb.AppendLine();
            }
            return sb.ToString();

            static string F(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);
        }

        // ---- Hesaplamalar ----
        private void RenumberZeroBased()
        {
            for (int i = 0; i < Waypoints.Count; i++) Waypoints[i].Index = i;
        }

        private void Recompute()
        {
            // Mesafe (X/Y = Lat/Lon)
            double km = 0.0;
            for (int i = 1; i < Waypoints.Count; i++)
            {
                var a = Waypoints[i - 1]; var b = Waypoints[i];
                if (a.HasValidXY && b.HasValidXY)
                    km += HaversineKm(a.X, a.Y, b.X, b.Y);
            }
            TotalDistanceKm = km;

            // WPL110’da hız yok → ETA basitçe 0
            EstimatedTimeMin = 0;

            OnPropertyChanged(nameof(Waypoints));
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371.0;
            double dLat = ToRad(lat2 - lat1);
            double dLon = ToRad(lon2 - lon1);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }
        private static double ToRad(double deg) => deg * Math.PI / 180.0;

        private static int ParseI(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        private static double ParseD(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        public string Validate()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Görev doğrulama raporu:");
            bool hasErr = false;
            for (int i = 0; i < Waypoints.Count; i++)
            {
                var w = Waypoints[i];
                string p = $"#{i} ";
                if (!w.HasValidXY) { sb.AppendLine(p + "HATA: Lat/Lon geçersiz."); hasErr = true; }
                if (double.IsNaN(w.Z)) { sb.AppendLine(p + "HATA: Alt geçersiz."); hasErr = true; }
                // WPL110'da ek alanlar (param1..4) isteğe bağlıdır
            }
            if (!hasErr) sb.AppendLine("OK: Kritik hata yok.");
            return sb.ToString();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // WPL110 satırı
    public class WaypointItem : INotifyPropertyChanged
    {
        // ZORUNLU alanlar
        int _index, _current, _frame, _command, _autocont;
        double _p1, _p2, _p3, _p4, _x, _y, _z;

        public int Index { get => _index; set { _index = value; OnPropertyChanged(nameof(Index)); } }
        public int Current { get => _current; set { _current = value; OnPropertyChanged(nameof(Current)); } }
        public int Frame { get => _frame; set { _frame = value; OnPropertyChanged(nameof(Frame)); } }
        public int Command { get => _command; set { _command = value; OnPropertyChanged(nameof(Command)); } }

        public double Param1 { get => _p1; set { _p1 = value; OnPropertyChanged(nameof(Param1)); } }
        public double Param2 { get => _p2; set { _p2 = value; OnPropertyChanged(nameof(Param2)); } }
        public double Param3 { get => _p3; set { _p3 = value; OnPropertyChanged(nameof(Param3)); } }
        public double Param4 { get => _p4; set { _p4 = value; OnPropertyChanged(nameof(Param4)); } }

        // X=lat, Y=lon, Z=alt
        public double X { get => _x; set { _x = value; OnPropertyChanged(nameof(X)); } }
        public double Y { get => _y; set { _y = value; OnPropertyChanged(nameof(Y)); } }
        public double Z { get => _z; set { _z = value; OnPropertyChanged(nameof(Z)); } }

        public int AutoContinue { get => _autocont; set { _autocont = value; OnPropertyChanged(nameof(AutoContinue)); } }

        // Yardımcı
        public bool HasValidXY =>
            !double.IsNaN(X) && !double.IsNaN(Y) &&
            X is >= -90 and <= 90 &&
            Y is >= -180 and <= 180;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
