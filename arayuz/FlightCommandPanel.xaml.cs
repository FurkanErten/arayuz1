using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace arayuz_deneme_1
{
    public partial class FlightCommandPanel : UserControl
    {
        private FlightCommandVM? _vm;

        public FlightCommandPanel()
        {
            InitializeComponent();

            Loaded += (_, __) => ApplyState();
            SizeChanged += (_, __) => ApplyState();
            Unloaded += (_, __) => DisposeVm();
        }

        public IMavlinkClient? Mav
        {
            get => (IMavlinkClient?)GetValue(MavProperty);
            set => SetValue(MavProperty, value);
        }

        public static readonly DependencyProperty MavProperty =
            DependencyProperty.Register(
                nameof(Mav),
                typeof(IMavlinkClient),
                typeof(FlightCommandPanel),
                new PropertyMetadata(null, OnMavChanged));

        private static void OnMavChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = (FlightCommandPanel)d;
            var newMav = e.NewValue as IMavlinkClient;

            // SetClient yoksa en stabil: VM recreate
            panel.DisposeVm();

            if (newMav == null)
            {
                panel.DataContext = null;
                return;
            }

            panel._vm = new FlightCommandVM(newMav);
            panel.DataContext = panel._vm;
        }

        private void DisposeVm()
        {
            if (_vm is IDisposable disp) disp.Dispose();
            _vm = null;
        }

        private void ApplyState()
        {
            double w = ActualWidth;

            if (w >= 700)
                VisualStateManager.GoToState(this, "Wide", true);
            else if (w >= 480)
                VisualStateManager.GoToState(this, "Medium", true);
            else
                VisualStateManager.GoToState(this, "Narrow", true);
        }

        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }
}
