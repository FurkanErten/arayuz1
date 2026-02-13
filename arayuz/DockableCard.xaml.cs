using System.Windows;
using System.Windows.Controls;

namespace arayuz_deneme_1
{
    public partial class DockableCard : UserControl
    {
        // Başlık için DP
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(DockableCard),
                new PropertyMetadata(string.Empty));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        // İçerik için ayrı DP (Content yerine Child kullanıyoruz)
        public static readonly DependencyProperty ChildProperty =
            DependencyProperty.Register(
                nameof(Child),
                typeof(UIElement),
                typeof(DockableCard),
                new PropertyMetadata(null));

        public UIElement? Child
        {
            get => (UIElement?)GetValue(ChildProperty);
            set => SetValue(ChildProperty, value);
        }

        // Float durumu
        private bool _isFloating;
        private Window? _floatWindow;
        private Panel? _originalParent;
        private int _originalIndex;

        public DockableCard()
        {
            InitializeComponent();
        }

        // HEADER: double click → float/dock
        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (_isFloating)
                    DockBack();
                else
                    FloatOut();
            }
        }

        private void FloatOut()
        {
            if (_isFloating) return;

            _originalParent = this.Parent as Panel;
            if (_originalParent == null)
            {
                MessageBox.Show(
                    "DockableCard bir Panel içinde değil. Float için Panel içinde olmalı.",
                    "DockableCard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _originalIndex = _originalParent.Children.IndexOf(this);

            // Parent'tan çıkar
            _originalParent.Children.Remove(this);

            // Float pencere
            _floatWindow = new Window
            {
                Title = Title,
                Content = this,
                Width = 600,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current.MainWindow,
                WindowStyle = WindowStyle.SingleBorderWindow
            };

            _floatWindow.Closed += FloatWindow_Closed;
            _isFloating = true;
            _floatWindow.Show();
        }

        private void DockBack()
        {
            if (!_isFloating)
                return;

            if (_floatWindow != null)
            {
                // recursion önle
                _floatWindow.Closed -= FloatWindow_Closed;
                _floatWindow.Content = null;
                _floatWindow.Close();
                _floatWindow = null;
            }

            if (_originalParent != null && !_originalParent.Children.Contains(this))
            {
                _originalParent.Children.Insert(
                    _originalIndex >= 0 ? _originalIndex : _originalParent.Children.Count,
                    this);
            }

            _isFloating = false;
        }

        private void FloatWindow_Closed(object? sender, System.EventArgs e)
        {
            // Kullanıcı X'e basarak kapattıysa
            if (_isFloating)
            {
                _floatWindow = null;

                if (_originalParent != null && !_originalParent.Children.Contains(this))
                {
                    _originalParent.Children.Insert(
                        _originalIndex >= 0 ? _originalIndex : _originalParent.Children.Count,
                        this);
                }

                _isFloating = false;
            }
        }
    }
}
