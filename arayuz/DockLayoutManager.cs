using AvalonDock;
using AvalonDock.Layout.Serialization;
using System.IO;

namespace arayuz_deneme_1
{
    public static class DockLayoutManager
    {
        public static void Save(DockingManager mgr, string path)
        {
            var serializer = new XmlLayoutSerializer(mgr);
            using var writer = new StreamWriter(path);
            serializer.Serialize(writer);
        }

        public static void Load(DockingManager mgr, string path)
        {
            if (!File.Exists(path)) return;

            var serializer = new XmlLayoutSerializer(mgr);

            // ContentId ile eşleştirerek panel içeriklerini geri bağlayacağız
            serializer.LayoutSerializationCallback += (s, e) =>
            {
                // e.Model.ContentId => "HUD", "VIDEO", "MAP", "TEL", "CMD"
                // WPF'de içerikler zaten XAML'de olduğu için çoğu zaman gerek kalmaz.
                // Ama float/pencere restore gibi durumlarda gerekebilir.
                // Şimdilik boş bırakıyorum.
            };

            using var reader = new StreamReader(path);
            serializer.Deserialize(reader);
        }
    }
}
