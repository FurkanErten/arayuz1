using System;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace arayuz_deneme_1
{
    public partial class WelcomeLogin : UserControl
    {
        public event EventHandler<string>? LoginSucceeded; // Başarılı giriş → kullanıcı adı döner

        // Demo kullanıcı
        private const string DemoUser = "lagari";
        // ⚠ Gerçek projede salt + iterasyon kullanmalısın!
        private static readonly string DemoPassHash = Sha256("1234");

        public WelcomeLogin()
        {
            InitializeComponent();

            // Ekran açılınca kullanıcı adı otomatik dolsun
            Loaded += (_, __) =>
            {
                TxtUser.Text = DemoUser;
                _ = TxtUser.Focus();
            };

            // Enter ile giriş desteği
            PreviewKeyDown += Root_PreviewKeyDown;
            TxtPass.PreviewKeyDown += Password_PreviewKeyDown;
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e) => TryLogin();

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TryLogin();
            }
        }

        private void Password_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TryLogin();
            }
        }

        private void TryLogin()
        {
            var user = (TxtUser.Text ?? string.Empty).Trim();
            var pass = TxtPass.Password ?? string.Empty;

            if (Validate(user, pass))
            {
                LblError.Visibility = Visibility.Collapsed;
                LoginSucceeded?.Invoke(this, user);
            }
            else
            {
                LblError.Text = "Kullanıcı adı veya şifre hatalı.";
                LblError.Visibility = Visibility.Visible;

                if (!string.IsNullOrEmpty(user))
                {
                    TxtPass.SelectAll();
                    _ = TxtPass.Focus();
                }
                else
                {
                    _ = TxtUser.Focus();
                }
            }
        }

        private static bool Validate(string user, string pass)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass))
                return false;

            if (!user.Equals(DemoUser, StringComparison.OrdinalIgnoreCase))
                return false;

            return Sha256(pass) == DemoPassHash;
        }

        private static string Sha256(string s)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(bytes); // .NET 5+
        }
    }
}
