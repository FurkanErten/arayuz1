// PlaneLayer.cs
using GMap.NET;
using GMap.NET.WindowsPresentation;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace arayuz_deneme_1
{
    public class PlaneLayer
    {
        private readonly GMapControl _map;
        private GMapMarker? _marker;
        private Image? _img;
        private readonly RotateTransform _rot = new RotateTransform(0);
        private double _lastLat, _lastLon;

        public PlaneLayer(GMapControl map, double initLat = 40.1955, double initLon = 29.0612)
        {
            _map = map;
            _lastLat = initLat; _lastLon = initLon;
        }

        public void Update(double lat, double lon, double headingDeg)
        {
            _lastLat = double.IsNaN(lat) ? _lastLat : lat;
            _lastLon = double.IsNaN(lon) ? _lastLon : lon;

            if (_marker == null)
            {
                ImageSource planeSource;
                try
                {
                    planeSource = new BitmapImage(new Uri(
                        @"C:\Users\ferte\source\repos\arayuz_deneme_1\arayuz_deneme_1\images\icon\plane.png",
                        UriKind.Absolute));
                }
                catch
                {
                    planeSource = new BitmapImage(new Uri("pack://application:,,,/Resources/plane.png", UriKind.Absolute));
                }

                _img = new Image
                {
                    Width = 48,
                    Height = 48,
                    Source = planeSource,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = _rot,
                    IsHitTestVisible = false
                };

                _marker = new GMapMarker(new PointLatLng(_lastLat, _lastLon))
                {
                    Shape = _img,
                    Offset = new Point(-24, -24)
                };
                _rot.Angle = headingDeg - 45.0;
                _map.Markers.Add(_marker);
            }
            else
            {
                _marker.Position = new PointLatLng(_lastLat, _lastLon);
                _rot.Angle = headingDeg - 45.0;
            }
        }

        public void CenterOnAircraft(bool center = true)
        {
            if (!center) return;
            _map.Position = new PointLatLng(_lastLat, _lastLon);
        }
    }
}
