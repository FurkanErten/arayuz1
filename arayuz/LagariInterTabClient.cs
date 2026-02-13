using System;
using System.Windows;
using Dragablz;

namespace arayuz_deneme_1
{
    /// <summary>
    /// Tab sürükleyip başka host'a atma + yeni pencereye (tear-out) çıkarmayı kontrol eder.
    /// </summary>
    public sealed class LagariInterTabClient : IInterTabClient
    {
        public static LagariInterTabClient Instance { get; } = new LagariInterTabClient();
        private LagariInterTabClient() { }

        /// <summary>
        /// Tab yeni pencereye çıkarıldığında oluşturulacak host (Window) burada üretilir.
        /// </summary>
        public INewTabHost<Window> GetNewHost(IInterTabClient interTabClient, object partition, TabablzControl source)
        {
            // Yeni pencere
            var win = new TearOutWindow();

            // Bu pencerenin içindeki TabablzControl'ü host olarak döndür
            return new NewTabHost<Window>(win, win.TabHost);
        }

        /// <summary>
        /// Bir tab başka bir TabablzControl'a taşınabilir mi?
        /// </summary>
        public TabEmptiedResponse TabEmptiedHandler(TabablzControl tabControl, Window window)
        {
            // Eğer bir pencere tamamen boşaldıysa kapatmak mantıklı:
            if (window is TearOutWindow)
            {
                window.Close();
                return TabEmptiedResponse.CloseWindowOrLayoutBranch;
            }

            // Ana pencerede boşalan branch vb. için default davranış:
            return TabEmptiedResponse.DoNothing;
        }
    }
}
