using arayuz_deneme_1.Net;
using AvalonDock.Layout;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsPresentation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace arayuz_deneme_1
{
    public partial class data : UserControl
    {
        private readonly Brush _dockIconWhite = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
        private DispatcherTimer? _dockDebounce;
        private DateTime _forceUntil;
        private bool _renderHooked;

        // ===================== MAP CACHE (NO OVERLAYS API) =====================
        private readonly Dictionary<int, GMapMarker> _uavMarkers = new();
        private GMapMarker? _qrMarker;

        // HSS circles (store as polygon markers)
        private readonly Dictionary<int, GMapPolygon> _hssCirclePoly = new();

        // MAP API SYNC (QR/HSS polling)
        private DispatcherTimer? _mapApiTimer;
        private SihaApiClient? _api;
        private int _selfTeamNo = -1;

        // ✅ map init guard + pending cache
        private bool _mapInitialized;
        private bool _mapReady;

        private readonly Dictionary<int, (double lat, double lon, double hdg, bool isSelf)> _pendingUavs = new();
        private (double lat, double lon)? _pendingQr;
        private List<HssCoord>? _pendingHss;

        public data()
        {
            InitializeComponent();
            if (_dockIconWhite.CanFreeze) ((SolidColorBrush)_dockIconWhite).Freeze();
        }

        public void BindCommands()
        {
            // placeholder
        }

        // ===================== DOCK HEADER ICON FIX (ORDER-BASED: ▼ PIN X) =====================
        private void DockingManager_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ForceDockHeaderIconsWhite), DispatcherPriority.Loaded);

            _dockDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _dockDebounce.Tick += (_, __) =>
            {
                _dockDebounce!.Stop();
                ForceDockHeaderIconsWhite();
            };

            DockingManager.LayoutUpdated += (_, __) =>
            {
                _dockDebounce?.Stop();
                _dockDebounce?.Start();
            };

            _forceUntil = DateTime.UtcNow.AddSeconds(1.2);
            if (!_renderHooked)
            {
                _renderHooked = true;
                CompositionTarget.Rendering += CompositionTarget_Rendering;
            }
        }

        private void CompositionTarget_Rendering(object? sender, EventArgs e)
        {
            if (DateTime.UtcNow <= _forceUntil)
            {
                ForceDockHeaderIconsWhite();
                return;
            }

            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _renderHooked = false;
        }

        private void ForceDockHeaderIconsWhite()
        {
            if (DockingManager == null) return;

            var iconButtons = FindVisualChildren<ButtonBase>(DockingManager)
                .Where(IsSmallHeaderIconButton)
                .ToList();

            var groups = iconButtons
                .Select(b => new { Btn = b, Host = FindHeaderHost(b) })
                .Where(x => x.Host != null)
                .GroupBy(x => x.Host!)
                .ToList();

            foreach (var g in groups)
            {
                var ordered = g.Select(x => x.Btn)
                               .Distinct()
                               .Select(b => new { Btn = b, X = GetXOnDock(b) })
                               .OrderBy(x => x.X)
                               .Select(x => x.Btn)
                               .ToList();

                for (int i = 0; i < ordered.Count; i++)
                {
                    DockIconKind kind;

                    if (ordered.Count >= 3)
                    {
                        kind = (i == 0) ? DockIconKind.DropDown
                             : (i == ordered.Count - 1) ? DockIconKind.CloseX
                             : DockIconKind.Pin;
                    }
                    else
                    {
                        kind = (i == ordered.Count - 1) ? DockIconKind.CloseX : DockIconKind.Pin;
                    }

                    ApplyPureWhiteIcon(ordered[i], kind);
                }
            }
        }

        private enum DockIconKind { DropDown, Pin, CloseX }

        private void ApplyPureWhiteIcon(ButtonBase b, DockIconKind kind)
        {
            foreach (var img in FindVisualChildren<Image>(b)) img.Opacity = 0.0;
            foreach (var p in FindVisualChildren<Path>(b)) p.Opacity = 0.0;
            foreach (var tb in FindVisualChildren<TextBlock>(b)) tb.Opacity = 0.0;

            b.Background = Brushes.Transparent;
            b.BorderBrush = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Cursor = Cursors.Hand;

            if (b.Width <= 0) b.Width = 18;
            if (b.Height <= 0) b.Height = 18;

            b.Template = MakeIconTemplate(kind);
        }

        private ControlTemplate MakeIconTemplate(DockIconKind kind)
        {
            string data = kind switch
            {
                DockIconKind.CloseX => "M4,4 L14,14 M14,4 L4,14",
                DockIconKind.DropDown => "M4,7 L9,12 L14,7",
                _ => "M9,3 L12,6 L10.5,7.5 L10.5,13 L9,15 L7.5,13 L7.5,7.5 L6,6 Z"
            };

            var template = new ControlTemplate(typeof(ButtonBase));

            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(FrameworkElement.WidthProperty, 18.0);
            grid.SetValue(FrameworkElement.HeightProperty, 18.0);

            var bg = new FrameworkElementFactory(typeof(Border));
            bg.Name = "Bg";
            bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            grid.AppendChild(bg);

            var icon = new FrameworkElementFactory(typeof(Path));
            icon.SetValue(Path.DataProperty, System.Windows.Media.Geometry.Parse(data));
            icon.SetValue(Path.StrokeProperty, _dockIconWhite);
            icon.SetValue(Path.FillProperty, Brushes.Transparent);
            icon.SetValue(Path.StrokeThicknessProperty, 2.0);
            icon.SetValue(Path.StrokeStartLineCapProperty, PenLineCap.Round);
            icon.SetValue(Path.StrokeEndLineCapProperty, PenLineCap.Round);
            icon.SetValue(FrameworkElement.WidthProperty, 14.0);
            icon.SetValue(FrameworkElement.HeightProperty, 14.0);
            icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grid.AppendChild(icon);

            template.VisualTree = grid;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), "Bg"));
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.90));
            template.Triggers.Add(hover);

            var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            press.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65));
            template.Triggers.Add(press);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
            template.Triggers.Add(disabled);

            return template;
        }

        private bool IsSmallHeaderIconButton(ButtonBase b)
        {
            double w = (b.ActualWidth > 0) ? b.ActualWidth : b.Width;
            double h = (b.ActualHeight > 0) ? b.ActualHeight : b.Height;

            bool sizeOk = (w > 0 && w <= 30) && (h > 0 && h <= 30);
            if (!sizeOk) return false;

            return HasAncestorTypeName(b,
                "Title", "Header", "LayoutAnchorable", "LayoutDocument", "Navigator", "Tab");
        }

        private DependencyObject? FindHeaderHost(DependencyObject d)
        {
            DependencyObject? cur = d;
            while (cur != null)
            {
                var tn = cur.GetType().Name;
                if (tn.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Header", StringComparison.OrdinalIgnoreCase) >= 0)
                    return cur;

                cur = VisualTreeHelper.GetParent(cur);
            }
            return null;
        }

        private double GetXOnDock(FrameworkElement e)
        {
            try
            {
                var pt = e.TransformToAncestor(DockingManager).Transform(new Point(0, 0));
                return pt.X;
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private bool HasAncestorTypeName(DependencyObject d, params string[] containsAny)
        {
            DependencyObject? cur = d;
            while (cur != null)
            {
                var tn = cur.GetType().Name;
                foreach (var key in containsAny)
                {
                    if (tn.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;

                foreach (var sub in FindVisualChildren<T>(child))
                    yield return sub;
            }
        }

        // ===================== VIDEO =====================
        private async void BtnConnectVideo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = VideoUrlTextBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(url)) return;

                if (web != null && web.CoreWebView2 == null)
                    await web.EnsureCoreWebView2Async();

                if (web != null)
                    web.Source = new Uri(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Video URL hatası: {ex.Message}");
            }
        }

        // ===================== MAP =====================
        private void GMap_Loaded(object sender, RoutedEventArgs e)
        {
            // ✅ Loaded bazen tekrar çalışıyor -> eski markerları siliyordu
            if (_mapInitialized)
            {
                _mapReady = true;
                RedrawPendingIfAny();
                return;
            }

            _mapInitialized = true;

            try
            {
                GMap.MapProvider = GoogleSatelliteMapProvider.Instance;
                GMaps.Instance.Mode = AccessMode.ServerAndCache;

                GMap.MinZoom = 2;
                GMap.MaxZoom = 19;
                GMap.Zoom = 15;

                GMap.Position = new PointLatLng(40.195, 29.060);
                GMap.ShowCenter = false;

                GMap.DragButton = MouseButton.Left;

                GMap.Markers.Clear();
                _uavMarkers.Clear();
                _hssCirclePoly.Clear();
                _qrMarker = null;

                _mapReady = true;
                RedrawPendingIfAny();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"GMap init hatası: {ex.Message}");
            }
        }

        private void GMap_MouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                var p = e.GetPosition(GMap);
                var latLng = GMap.FromLocalToLatLng((int)p.X, (int)p.Y);

                if (CoordText != null)
                    CoordText.Text = $"Lat: {latLng.Lat:0.000000}, Lon: {latLng.Lng:0.000000}";
            }
            catch { }
        }

        private void BtnCenter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GMap.Position = new PointLatLng(40.195, 29.060);
                GMap.Zoom = 16;
            }
            catch { }
        }

        // ===================== MAP SYNC (QR/HSS polling) =====================
        public void StartMapSync(SihaApiClient api, int selfTeamNo)
        {
            _api = api;
            _selfTeamNo = selfTeamNo;

            _mapApiTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _mapApiTimer.Tick -= MapApiTimer_Tick;
            _mapApiTimer.Tick += MapApiTimer_Tick;
            _mapApiTimer.Start();
        }

        public void StopMapSync()
        {
            try { _mapApiTimer?.Stop(); } catch { }
        }
        private DateTime _lastErrShown = DateTime.MinValue;
        private async void MapApiTimer_Tick(object? sender, EventArgs e)
        {

            if (_api == null) return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

                var qr = await _api.GetQrAsync(cts.Token);
                if (qr != null)
                {
                    _pendingQr = (qr.qrEnlem, qr.qrBoylam);
                    if (_mapReady) DrawOrUpdateQr(qr.qrEnlem, qr.qrBoylam);
                }

                var hss = await _api.GetHssAsync(cts.Token);
                if (hss?.hss_koordinat_bilgileri != null)
                {
                    System.Diagnostics.Debug.WriteLine($"HSS count = {hss.hss_koordinat_bilgileri.Length}, " +
                                   $"first=({hss.hss_koordinat_bilgileri[0].hssEnlem},{hss.hss_koordinat_bilgileri[0].hssBoylam}) r={hss.hss_koordinat_bilgileri[0].hssYaricap}");


                    _pendingHss = hss.hss_koordinat_bilgileri.ToList();
                    if (_mapReady) DrawOrUpdateHssCircles(hss.hss_koordinat_bilgileri);
                }

                if (_mapReady)
                    GMap.InvalidateVisual();

            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - _lastErrShown > TimeSpan.FromSeconds(3))
                {
                    _lastErrShown = DateTime.UtcNow;
                    MessageBox.Show(ex.ToString(), "MapApiTimer_Tick ERROR");
                }
            }
        }

        // ===================== SERVER/MAVLINK TELEMETRY -> MAP =====================
        public void ApplyTelemetryToMap(TelemetryPayload payload, TelemetryResponse? resp)
        {
            _pendingUavs[payload.takim_numarasi] = (payload.iha_enlem, payload.iha_boylam, payload.iha_yonelme, true);

            if (resp?.konumBilgileri != null)
            {
                foreach (var u in resp.konumBilgileri)
                    _pendingUavs[u.takim_numarasi] = (u.iha_enlem, u.iha_boylam, u.iha_yonelme, u.takim_numarasi == _selfTeamNo);
            }

            if (!_mapReady) return;

            DrawOrUpdateUav(payload.takim_numarasi, payload.iha_enlem, payload.iha_boylam, payload.iha_yonelme, isSelf: true);

            if (resp?.konumBilgileri != null)
            {
                foreach (var u in resp.konumBilgileri)
                {
                    bool isSelf = (u.takim_numarasi == _selfTeamNo);
                    DrawOrUpdateUav(u.takim_numarasi, u.iha_enlem, u.iha_boylam, u.iha_yonelme, isSelf);
                }
            }

            GMap.InvalidateVisual();
        }

        private void RedrawPendingIfAny()
        {
            if (!_mapReady) return;

            foreach (var kv in _pendingUavs)
            {
                var team = kv.Key;
                var (lat, lon, hdg, isSelf) = kv.Value;
                DrawOrUpdateUav(team, lat, lon, hdg, isSelf);
            }

            if (_pendingQr is { } qr)
                DrawOrUpdateQr(qr.lat, qr.lon);

            if (_pendingHss != null && _pendingHss.Count > 0)
                DrawOrUpdateHssCircles(_pendingHss);

            GMap.InvalidateVisual();
        }

        // ===================== DRAW: QR =====================
        private void DrawOrUpdateQr(double lat, double lon)
        {
            var pos = new PointLatLng(lat, lon);

            if (_qrMarker == null)
            {
                var el = new Ellipse
                {
                    Width = 14,
                    Height = 14,
                    Fill = Brushes.LimeGreen,
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                    ToolTip = "QR"
                };

                _qrMarker = new GMapMarker(pos)
                {
                    Shape = el,
                    Offset = new Point(-7, -7),
                    ZIndex = 1000
                };

                GMap.Markers.Add(_qrMarker);
            }
            else
            {
                _qrMarker.Position = pos;
            }
        }

        private void DrawOrUpdateHssCircles(IEnumerable<HssCoord> hssList)
        {
            var list = hssList?.ToList() ?? new List<HssCoord>();
            var ids = new HashSet<int>(list.Select(x => x.id));

            // silinenleri kaldır
            foreach (var id in _hssCirclePoly.Keys.ToList())
            {
                if (!ids.Contains(id))
                {
                    GMap.Markers.Remove(_hssCirclePoly[id]);
                    _hssCirclePoly.Remove(id);
                }
            }

            foreach (var h in list)
            {
                var center = new PointLatLng(h.hssEnlem, h.hssBoylam);
                var circlePts = GeoUtil.BuildCircle(center, h.hssYaricap, segments: 120);

                if (!_hssCirclePoly.TryGetValue(h.id, out var poly))
                {
                    poly = new GMapPolygon(circlePts) { ZIndex = 50 };
                    _hssCirclePoly[h.id] = poly;

                    GMap.Markers.Add(poly);

                    // ✅ shape'i GMap üretsin
                    GMap.RegenerateShape(poly);

                    // ✅ üretilen shape'i boya
                    ApplyHssStyle(poly, h);
                }
                else
                {
                    poly.Points.Clear();
                    foreach (var p in circlePts) poly.Points.Add(p);

                    GMap.RegenerateShape(poly);
                    ApplyHssStyle(poly, h);
                }

                Debug.WriteLine($"[HSS] id={h.id} pts={circlePts.Count} shape={poly.Shape?.GetType().Name ?? "null"}");
            }

            GMap.InvalidateVisual();
        }

        private void ApplyHssStyle(GMapPolygon poly, HssCoord h)
        {
            if (poly.Shape is System.Windows.Shapes.Path path)
            {
                path.Stroke = Brushes.Red;
                path.StrokeThickness = 3;
                path.Fill = new SolidColorBrush(Color.FromArgb(60, 255, 0, 0));
                path.ToolTip = $"HSS {h.id} (r={h.hssYaricap}m)";
                path.IsHitTestVisible = false;

                // bazen z-index shape'te önemli
                Panel.SetZIndex(path, 50);
            }
            else
            {
                // ✅ burada null geliyorsa asıl problem bu
                Debug.WriteLine("[HSS] poly.Shape is null OR not Path");
            }
        }


        // ===================== DRAW: UAV =====================
        private void DrawOrUpdateUav(int teamNo, double lat, double lon, double headingDeg, bool isSelf)
        {
            var pos = new PointLatLng(lat, lon);

            if (!_uavMarkers.TryGetValue(teamNo, out var marker))
            {
                var stroke = isSelf ? Brushes.White : Brushes.Cyan;
                var fill = isSelf
                    ? new SolidColorBrush(Color.FromArgb(210, 46, 204, 113))
                    : new SolidColorBrush(Color.FromArgb(210, 59, 130, 246));

                var ctrl = new UavMarkerControl(stroke, fill);
                ctrl.SetHeading(headingDeg);
                ctrl.ToolTip = $"Takım {teamNo}";

                marker = new GMapMarker(pos)
                {
                    Shape = ctrl,
                    Offset = new Point(-17, -17),
                    ZIndex = isSelf ? 900 : 800
                };

                _uavMarkers[teamNo] = marker;
                GMap.Markers.Add(marker);
            }
            else
            {
                marker.Position = pos;

                if (marker.Shape is UavMarkerControl ctrl)
                {
                    ctrl.SetHeading(headingDeg);

                    var stroke = isSelf ? Brushes.White : Brushes.Cyan;
                    var fill = isSelf
                        ? new SolidColorBrush(Color.FromArgb(210, 46, 204, 113))
                        : new SolidColorBrush(Color.FromArgb(210, 59, 130, 246));

                    ctrl.SetColors(stroke, fill);
                }
            }
        }

        // ===================== ✅ PANELS MENU API =====================
        public void SetPanelVisible(string panelKey, bool visible)
        {
            if (DockingManager?.Layout == null) return;

            string id = panelKey switch
            {
                "HUD" => "HUD",
                "Telemetry" => "TEL",
                "Camera" => "VIDEO",
                "ControlPanel" => "CMD",
                "Map" => "MAP",
                _ => panelKey
            };

            var anchorable = DockingManager.Layout
                .Descendents()
                .OfType<LayoutAnchorable>()
                .FirstOrDefault(a => string.Equals(a.ContentId, id, StringComparison.OrdinalIgnoreCase));

            if (anchorable == null) return;

            if (visible)
            {
                anchorable.Show();
                anchorable.IsActive = true;
                anchorable.IsSelected = true;
            }
            else
            {
                anchorable.Hide();
            }
        }

        public bool IsPanelVisible(string panelKey)
        {
            if (DockingManager?.Layout == null) return false;

            string id = panelKey switch
            {
                "HUD" => "HUD",
                "Telemetry" => "TEL",
                "Camera" => "VIDEO",
                "ControlPanel" => "CMD",
                "Map" => "MAP",
                _ => panelKey
            };

            var anchorable = DockingManager.Layout
                .Descendents()
                .OfType<LayoutAnchorable>()
                .FirstOrDefault(a => string.Equals(a.ContentId, id, StringComparison.OrdinalIgnoreCase));

            return anchorable?.IsVisible ?? false;
        }
    }

    // ===================== GEO HELPERS =====================
    internal static class GeoUtil
    {
        private const double R = 6378137.0; // meters

        public static List<PointLatLng> BuildCircle(PointLatLng center, double radiusMeters, int segments = 96)
        {
            var pts = new List<PointLatLng>(segments + 1);
            double lat = Deg2Rad(center.Lat);
            double lon = Deg2Rad(center.Lng);

            double d = radiusMeters / R;

            for (int i = 0; i <= segments; i++)
            {
                double brg = 2.0 * Math.PI * i / segments;

                double lat2 = Math.Asin(Math.Sin(lat) * Math.Cos(d) + Math.Cos(lat) * Math.Sin(d) * Math.Cos(brg));
                double lon2 = lon + Math.Atan2(Math.Sin(brg) * Math.Sin(d) * Math.Cos(lat),
                                               Math.Cos(d) - Math.Sin(lat) * Math.Sin(lat2));

                pts.Add(new PointLatLng(Rad2Deg(lat2), Rad2Deg(lon2)));
            }
            return pts;
        }

        private static double Deg2Rad(double d) => d * Math.PI / 180.0;
        private static double Rad2Deg(double r) => r * 180.0 / Math.PI;
    }

    // ===================== UAV MARKER CONTROL (ROTATABLE) =====================
    internal sealed class UavMarkerControl : Grid
    {
        private readonly Path _body;
        private readonly RotateTransform _rot;

        public UavMarkerControl(Brush stroke, Brush fill)
        {
            Width = 34;
            Height = 34;
            IsHitTestVisible = false;

            _rot = new RotateTransform(0, 17, 17);

            _body = new Path
            {
                Data = System.Windows.Media.Geometry.Parse("M17,2 L21,14 L30,17 L21,20 L17,32 L13,20 L4,17 L13,14 Z"),
                Stroke = stroke,
                Fill = fill,
                StrokeThickness = 1.8,
                RenderTransform = _rot,
                RenderTransformOrigin = new Point(0.5, 0.5),
                SnapsToDevicePixels = true
            };

            Children.Add(_body);
        }

        public void SetHeading(double headingDeg)
        {
            // Eğer 90° kayık görürsen: _rot.Angle = headingDeg + 90;
            _rot.Angle = headingDeg;
        }

        public void SetColors(Brush stroke, Brush fill)
        {
            _body.Stroke = stroke;
            _body.Fill = fill;
        }
    }
}
