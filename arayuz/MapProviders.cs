// MapProviders.cs — Sürüm-uyumlu MBTiles HTTP provider (GMap.NET)
using System;
using System.IO;
using System.Net;
using System.Reflection;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.Projections;

namespace arayuz_deneme_1
{
    public sealed class MbtilesHttpProvider : GMapProvider
    {
        public static MbtilesHttpProvider Instance { get; } = new MbtilesHttpProvider();

        // İstersen şablonu runtime’da değiştir:
        public static void SetTemplate(string template) =>
            ((MbtilesHttpProvider)Instance)._template = template;

        public override Guid Id { get; } = Guid.Parse("b2a1e8a9-64d1-439f-9f2a-9a6d9a0a3c11");
        public override string Name => "MBTilesHTTP";
        public override PureProjection Projection => MercatorProjection.Instance;
        public override GMapProvider[] Overlays => new GMapProvider[] { Instance };

        // Senin server: /tiles/{z}/{x}/{y}  (uzantısız)
        private string _template = "http://127.0.0.1:5000/tiles/{z}/{x}/{y}";

        private MbtilesHttpProvider()
        {
            MinZoom = 3;
            MaxZoom = 19;
        }

        // Zorunlu override — tüm sürümlerde mevcut
        public override PureImage GetTileImage(GPoint pos, int zoom)
        {
            var url = _template
                .Replace("{z}", zoom.ToString())
                .Replace("{x}", pos.X.ToString())
                .Replace("{y}", pos.Y.ToString());

            // 1) Base sınıftaki GetTileImageUsingHttp(string) varsa onu kullan (reflection)
            try
            {
                var mi = typeof(GMapProvider).GetMethod(
                    "GetTileImageUsingHttp",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { typeof(string) },
                    null
                );
                if (mi != null)
                {
                    var img = mi.Invoke(this, new object[] { url }) as PureImage;
                    if (img != null) return img;
                }
            }
            catch { /* yut */ }

            // 2) Manuel indir + mevcut ImageProxy ile PureImage üret
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Proxy = null;
                    var bytes = wc.DownloadData(url);
                    if (bytes == null || bytes.Length == 0) return null;

                    // GMaps.Instance.ImageProxy.{FromArray|FromStream} dene (sürüm bağımsız)
                    var proxyProp = typeof(GMaps).GetProperty("ImageProxy", BindingFlags.Instance | BindingFlags.Public);
                    var proxy = proxyProp?.GetValue(GMaps.Instance);
                    if (proxy != null)
                    {
                        // FromArray(byte[])
                        var mArray = proxy.GetType().GetMethod("FromArray", new[] { typeof(byte[]) });
                        if (mArray != null)
                        {
                            var pi = mArray.Invoke(proxy, new object[] { bytes }) as PureImage;
                            if (pi != null) return pi;
                        }
                        // FromStream(Stream)
                        var mStream = proxy.GetType().GetMethod("FromStream", new[] { typeof(Stream) });
                        if (mStream != null)
                        {
                            using (var ms = new MemoryStream(bytes))
                            {
                                var pi = mStream.Invoke(proxy, new object[] { ms }) as PureImage;
                                if (pi != null) return pi;
                            }
                        }
                    }
                }
            }
            catch { /* yut */ }

            // 3) Olmadıysa tile yok/hata -> null
            return null;
        }
    }
}
