// PanelGap.cs
using System.Windows;
using System.Windows.Controls;

namespace arayuz_deneme_1
{
    public static class PanelGap
    {
        public static double GetGap(Panel obj) => (double)obj.GetValue(GapProperty);
        public static void SetGap(Panel obj, double value) => obj.SetValue(GapProperty, value);

        public static readonly DependencyProperty GapProperty =
            DependencyProperty.RegisterAttached(
                "Gap", typeof(double), typeof(PanelGap),
                new PropertyMetadata(0.0, OnGapChanged));

        private static void OnGapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Panel panel) return;

            void Apply()
            {
                double gap = GetGap(panel);
                for (int i = 0; i < panel.Children.Count; i++)
                {
                    if (panel.Children[i] is FrameworkElement fe)
                        fe.Margin = new Thickness(0, i == 0 ? 0 : gap, 0, 0);
                }
            }

            // İlk yüklemede ve çocuklar değişirse tekrar uygula
            panel.Loaded += (_, __) => Apply();
            panel.LayoutUpdated += (_, __) => Apply();
        }
    }
}
