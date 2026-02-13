using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace arayuz_deneme_1
{
    public partial class SetupPanel : UserControl
    {
        public event EventHandler<ThemeSettings>? Applied;
        public event EventHandler? Canceled;
        public event EventHandler? Reset;

        private ThemeSettings _snapshot;

        public ObservableCollection<ThemeProfile> Profiles { get; } = new();

        private const string ProfileFileName = "theme_profiles.json";

        public SetupPanel()
        {
            InitializeComponent();

            _snapshot = GetCurrentFromUi();

            ApplyButton.Click += (_, __) => Apply();
            CancelButton.Click += (_, __) => Cancel();
            ResetButton.Click += (_, __) => ResetAll();

            ThemeLight.Checked += (_, __) =>
            {
                ThemeDark.IsChecked = false;
                ThemeAuto.IsChecked = false;
                ApplyThemePreset(ThemeMode.Light);
            };
            ThemeDark.Checked += (_, __) =>
            {
                ThemeLight.IsChecked = false;
                ThemeAuto.IsChecked = false;
                ApplyThemePreset(ThemeMode.Dark);
            };
            ThemeAuto.Checked += (_, __) =>
            {
                ThemeLight.IsChecked = false;
                ThemeDark.IsChecked = false;
                ApplyThemePreset(ThemeMode.Auto);
            };

            HookLivePreview();

            LoadProfiles();
            ProfileList.ItemsSource = Profiles;
            ProfileList.MouseDoubleClick += (_, __) => ApplySelectedProfile();

            UpdatePreview(GetCurrentFromUi(), "(Geçici)");
        }

        private void HookLivePreview()
        {
            void onTextChange(object? s, TextChangedEventArgs e) =>
                UpdatePreview(GetCurrentFromUi(), GetActiveProfileNameOrDefault());

            void onValueChange(object? s, RoutedPropertyChangedEventArgs<double> e) =>
                UpdatePreview(GetCurrentFromUi(), GetActiveProfileNameOrDefault());

            AccentHex.TextChanged += onTextChange;
            BgHex.TextChanged += onTextChange;
            TextHex.TextChanged += onTextChange;

            AccentR.ValueChanged += onValueChange;
            AccentG.ValueChanged += onValueChange;
            AccentB.ValueChanged += onValueChange;

            FontSizeSlider.ValueChanged += onValueChange;
            CornerSlider.ValueChanged += onValueChange;
            SpacingSlider.ValueChanged += onValueChange;
            ScaleSlider.ValueChanged += onValueChange;
            DensitySlider.ValueChanged += onValueChange;

            HighContrast.Checked += HighContrast_Checked;
            HighContrast.Unchecked += HighContrast_Checked;
        }

        // Tema presetleri
        private void ApplyThemePreset(ThemeMode mode)
        {
            if (mode == ThemeMode.Light)
            {
                SetBgColor(ParseColor("#F3F4F6"), updatePreview: false);
                SetTextColor(ParseColor("#0F172A"), updatePreview: false);
                SetAccentColor(ParseColor("#2563EB"), updatePreview: false);
            }
            else if (mode == ThemeMode.Dark)
            {
                SetBgColor(ParseColor("#101319"), updatePreview: false);
                SetTextColor(Colors.White, updatePreview: false);
                SetAccentColor(ParseColor("#3B82F6"), updatePreview: false);
            }
            else // Auto -> şimdilik koyu kabul
            {
                SetBgColor(ParseColor("#020617"), updatePreview: false);
                SetTextColor(ParseColor("#E5E7EB"), updatePreview: false);
                SetAccentColor(ParseColor("#3B82F6"), updatePreview: false);
            }

            var s = GetCurrentFromUi();
            s.Theme = mode;
            UpdatePreview(s, GetActiveProfileNameOrDefault());
        }

        private void Apply()
        {
            var s = GetCurrentFromUi();
            _snapshot = s;
            UpdatePreview(s, GetActiveProfileNameOrDefault());
            Applied?.Invoke(this, s);
        }

        private void Cancel()
        {
            SetUiFromSettings(_snapshot);
            UpdatePreview(_snapshot, GetActiveProfileNameOrDefault());
            Canceled?.Invoke(this, EventArgs.Empty);
        }

        private void ResetAll()
        {
            var defaults = ThemeSettings.Defaults();
            SetUiFromSettings(defaults);
            UpdatePreview(defaults, "(Varsayılan)");
            Reset?.Invoke(this, EventArgs.Empty);
        }

        // ---- UI <-> Settings ----
        private ThemeSettings GetCurrentFromUi()
        {
            string hex(TextBox tb, string fallback) => (tb.Text ?? fallback).Trim();
            double dbl(Slider sl, double fallback) => sl.Value;

            var accentFromRgb = Color.FromRgb(
                (byte)dbl(AccentR, 59),
                (byte)dbl(AccentG, 130),
                (byte)dbl(AccentB, 246)
            );

            var s = new ThemeSettings
            {
                Theme = GetThemeMode(),
                Accent = accentFromRgb,
                Background = TryParseHexColor(hex(BgHex, "#101319"), out var bg)
                    ? bg
                    : ParseColor("#101319"),
                Foreground = TryParseHexColor(hex(TextHex, "#FFFFFF"), out var fg)
                    ? fg
                    : Colors.White,
                BaseFontFamily = new FontFamily(
                    (FontFamilyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Segoe UI"),
                BaseFontSize = dbl(FontSizeSlider, 14),
                CornerRadius = dbl(CornerSlider, 12),
                Spacing = dbl(SpacingSlider, 8),
                UiScale = dbl(ScaleSlider, 1.0),
                Density = dbl(DensitySlider, 1.0),
                HighContrast = HighContrast.IsChecked == true,
                UseMica = UseMica.IsChecked == true,
                AnimationsEnabled = AnimatedTransitions.IsChecked == true
            };
            return s;
        }

        private void SetUiFromSettings(ThemeSettings s)
        {
            SetAccentColor(s.Accent, updatePreview: false);

            BgHex.Text = s.Background.ToString();
            TextHex.Text = s.Foreground.ToString();

            FontSizeSlider.Value = s.BaseFontSize;
            CornerSlider.Value = s.CornerRadius;
            SpacingSlider.Value = s.Spacing;
            ScaleSlider.Value = s.UiScale;
            DensitySlider.Value = s.Density;

            var family = s.BaseFontFamily.Source;
            var item = FontFamilyBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, family, StringComparison.OrdinalIgnoreCase));
            if (item != null)
                FontFamilyBox.SelectedItem = item;

            ThemeLight.IsChecked = s.Theme == ThemeMode.Light;
            ThemeDark.IsChecked = s.Theme == ThemeMode.Dark;
            ThemeAuto.IsChecked = s.Theme == ThemeMode.Auto;

            HighContrast.IsChecked = s.HighContrast;
            UseMica.IsChecked = s.UseMica;
            AnimatedTransitions.IsChecked = s.AnimationsEnabled;
        }

        private ThemeMode GetThemeMode()
        {
            if (ThemeLight.IsChecked == true) return ThemeMode.Light;
            if (ThemeAuto.IsChecked == true) return ThemeMode.Auto;
            return ThemeMode.Dark;
        }

        // Önizleme
        private void UpdatePreview(ThemeSettings s, string? activeProfileName = null)
        {
            Color bg = s.Background;
            Color fg = s.Foreground;
            Color accent = s.Accent;

            if (s.HighContrast)
            {
                bg = Colors.Black;
                fg = Colors.White;
                accent = Colors.Yellow;
            }

            Resources["App.Bg"] = new SolidColorBrush(bg);
            Resources["App.BgElev"] = new SolidColorBrush(Blend(bg, Colors.Black, 0.1));
            Resources["App.Card"] = new SolidColorBrush(Blend(bg, Colors.White, 0.03));
            Resources["App.Stroke"] = new SolidColorBrush(Blend(bg, Colors.White, 0.12));
            Resources["App.Text"] = new SolidColorBrush(fg);
            Resources["App.Muted"] = new SolidColorBrush(Blend(fg, bg, 0.5));
            Resources["App.Accent"] = new SolidColorBrush(accent);

            Resources["App.CornerRadius"] = new CornerRadius(s.CornerRadius);

            FontFamily = s.BaseFontFamily;
            FontSize = s.BaseFontSize;

            LayoutTransform = new ScaleTransform(s.UiScale, s.UiScale);

            ActiveProfileLabel.Text = $"Aktif profil: {activeProfileName ?? "(Geçici)"}";
        }

        public static void ApplyToApplication(ThemeSettings s)
        {
            var r = Application.Current.Resources;

            Color bg = s.Background;
            Color fg = s.Foreground;
            Color accent = s.Accent;

            if (s.HighContrast)
            {
                bg = Colors.Black;
                fg = Colors.White;
                accent = Colors.Yellow;
            }

            r["App.Bg"] = new SolidColorBrush(bg);
            r["App.BgElev"] = new SolidColorBrush(Blend(bg, Colors.Black, 0.1));
            r["App.Card"] = new SolidColorBrush(Blend(bg, Colors.White, 0.03));
            r["App.Stroke"] = new SolidColorBrush(Blend(bg, Colors.White, 0.12));
            r["App.Text"] = new SolidColorBrush(fg);
            r["App.Muted"] = new SolidColorBrush(Blend(fg, bg, 0.5));
            r["App.Accent"] = new SolidColorBrush(accent);
            r["App.CornerRadius"] = new CornerRadius(s.CornerRadius);
        }

        // ---- Renk swatch helper'ları ----
        private Color ParseColor(string hex) =>
            (Color)ColorConverter.ConvertFromString(hex)!;

        private void SetAccentColor(Color c, bool updatePreview = true)
        {
            AccentHex.Text = c.ToString();
            AccentR.Value = c.R;
            AccentG.Value = c.G;
            AccentB.Value = c.B;

            if (updatePreview)
                UpdatePreview(GetCurrentFromUi(), GetActiveProfileNameOrDefault());
        }

        private void SetBgColor(Color c, bool updatePreview = true)
        {
            BgHex.Text = c.ToString();
            if (updatePreview)
                UpdatePreview(GetCurrentFromUi(), GetActiveProfileNameOrDefault());
        }

        private void SetTextColor(Color c, bool updatePreview = true)
        {
            TextHex.Text = c.ToString();
            if (updatePreview)
                UpdatePreview(GetCurrentFromUi(), GetActiveProfileNameOrDefault());
        }

        private void AccentSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string hex)
            {
                SetAccentColor(ParseColor(hex));
            }
        }

        private void BgSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string hex)
            {
                SetBgColor(ParseColor(hex));
            }
        }

        private void TextSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string hex)
            {
                SetTextColor(ParseColor(hex));
            }
        }

        private void HighContrast_Checked(object? sender, RoutedEventArgs e)
        {
            UpdatePreview(GetCurrentFromUi(), GetActiveProfileNameOrDefault());
        }

        // ---- PROFİLLER ----
        private string GetProfileFilePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Lagari");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, ProfileFileName);
        }

        private void LoadProfiles()
        {
            Profiles.Clear();
            try
            {
                var path = GetProfileFilePath();
                if (!File.Exists(path))
                    return;

                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<ThemeProfileData[]>(json);
                if (data == null) return;

                foreach (var d in data)
                {
                    Profiles.Add(new ThemeProfile
                    {
                        Name = d.Name,
                        Settings = d.ToSettings()
                    });
                }
            }
            catch
            {
                // bozuk json vs.
            }

            if (!Profiles.Any())
            {
                Profiles.Add(new ThemeProfile
                {
                    Name = "Varsayılan",
                    Settings = ThemeSettings.Defaults()
                });
            }
        }

        private void SaveProfiles()
        {
            try
            {
                var path = GetProfileFilePath();
                var data = Profiles.Select(p => ThemeProfileData.From(p)).ToArray();
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(path, json);
            }
            catch
            {
                // yazamazsa sessiz geç
            }
        }

        private string GetActiveProfileNameOrDefault()
        {
            if (ProfileList.SelectedItem is ThemeProfile p)
                return p.Name;
            return "(Geçici)";
        }

        private void ProfileSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var name = (ProfileNameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                name = "Profil";

            var current = GetCurrentFromUi();

            var existing = Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Settings = current;
                ProfileList.SelectedItem = existing;
            }
            else
            {
                var profile = new ThemeProfile
                {
                    Name = name,
                    Settings = current
                };
                Profiles.Add(profile);
                ProfileList.SelectedItem = profile;
            }

            SaveProfiles();
            UpdatePreview(current, name);
        }

        private void ProfileApplyButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySelectedProfile();
        }

        private void ProfileDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileList.SelectedItem is not ThemeProfile selected)
                return;

            var idx = ProfileList.SelectedIndex;
            Profiles.Remove(selected);
            SaveProfiles();

            if (Profiles.Count > 0)
            {
                // Math.Clamp yerine elle clamp
                int newIndex = idx - 1;
                if (newIndex < 0) newIndex = 0;
                if (newIndex >= Profiles.Count) newIndex = Profiles.Count - 1;

                ProfileList.SelectedIndex = newIndex;
                if (ProfileList.SelectedItem is ThemeProfile p)
                {
                    SetUiFromSettings(p.Settings);
                    UpdatePreview(p.Settings, p.Name);
                }
            }
            else
            {
                var def = ThemeSettings.Defaults();
                SetUiFromSettings(def);
                UpdatePreview(def, "(Varsayılan)");
            }
        }

        private void ApplySelectedProfile()
        {
            if (ProfileList.SelectedItem is not ThemeProfile selected)
                return;

            SetUiFromSettings(selected.Settings);
            UpdatePreview(selected.Settings, selected.Name);
            Applied?.Invoke(this, selected.Settings);
        }

        // ---- Yardımcılar ----
        private static bool TryParseHexColor(string input, out Color color)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(input)!;
                color = c;
                return true;
            }
            catch
            {
                color = Colors.Transparent;
                return false;
            }
        }

        private static Color Blend(Color c1, Color c2, double t)
        {
            byte Lerp(byte a, byte b) => (byte)(a + (b - a) * t);
            return Color.FromArgb(255,
                Lerp(c1.R, c2.R),
                Lerp(c1.G, c2.G),
                Lerp(c1.B, c2.B));
        }
    }

    public enum ThemeMode { Light, Dark, Auto }

    public class ThemeSettings : EventArgs
    {
        public ThemeMode Theme { get; set; }
        public Color Accent { get; set; }
        public Color Background { get; set; }
        public Color Foreground { get; set; }
        public FontFamily BaseFontFamily { get; set; } = new("Segoe UI");
        public double BaseFontSize { get; set; } = 14;
        public double CornerRadius { get; set; } = 12;
        public double Spacing { get; set; } = 8;
        public double UiScale { get; set; } = 1.0;
        public double Density { get; set; } = 1.0;
        public bool HighContrast { get; set; }
        public bool UseMica { get; set; }
        public bool AnimationsEnabled { get; set; } = true;

        public static ThemeSettings Defaults() => new()
        {
            Theme = ThemeMode.Dark,
            Accent = (Color)ColorConverter.ConvertFromString("#3B82F6")!,
            Background = (Color)ColorConverter.ConvertFromString("#101319")!,
            Foreground = Colors.White,
            BaseFontFamily = new FontFamily("Segoe UI"),
            BaseFontSize = 14,
            CornerRadius = 12,
            Spacing = 8,
            UiScale = 1.0,
            Density = 1.0,
            HighContrast = false,
            UseMica = false,
            AnimationsEnabled = true
        };
    }

    public class ThemeProfile
    {
        public string Name { get; set; } = "";
        public ThemeSettings Settings { get; set; } = ThemeSettings.Defaults();
        public override string ToString() => Name;
    }

    public class ThemeProfileData
    {
        public string Name { get; set; } = "";
        public string Theme { get; set; } = "Dark";
        public string Accent { get; set; } = "#3B82F6";
        public string Background { get; set; } = "#101319";
        public string Foreground { get; set; } = "#FFFFFF";
        public string FontFamily { get; set; } = "Segoe UI";
        public double BaseFontSize { get; set; } = 14;
        public double CornerRadius { get; set; } = 12;
        public double Spacing { get; set; } = 8;
        public double UiScale { get; set; } = 1.0;
        public double Density { get; set; } = 1.0;
        public bool HighContrast { get; set; }
        public bool UseMica { get; set; }
        public bool AnimationsEnabled { get; set; } = true;

        public ThemeSettings ToSettings()
        {
            ThemeMode mode = ThemeMode.Dark;
            Enum.TryParse(Theme, true, out mode);

            Color ParseColorSafe(string hex, string fallback)
            {
                try { return (Color)ColorConverter.ConvertFromString(hex)!; }
                catch { return (Color)ColorConverter.ConvertFromString(fallback)!; }
            }

            return new ThemeSettings
            {
                Theme = mode,
                Accent = ParseColorSafe(Accent, "#3B82F6"),
                Background = ParseColorSafe(Background, "#101319"),
                Foreground = ParseColorSafe(Foreground, "#FFFFFF"),
                BaseFontFamily = new FontFamily(string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily),
                BaseFontSize = BaseFontSize,
                CornerRadius = CornerRadius,
                Spacing = Spacing,
                UiScale = UiScale,
                Density = Density,
                HighContrast = HighContrast,
                UseMica = UseMica,
                AnimationsEnabled = AnimationsEnabled
            };
        }

        public static ThemeProfileData From(ThemeProfile p)
        {
            var s = p.Settings;
            return new ThemeProfileData
            {
                Name = p.Name,
                Theme = s.Theme.ToString(),
                Accent = s.Accent.ToString(),
                Background = s.Background.ToString(),
                Foreground = s.Foreground.ToString(),
                FontFamily = s.BaseFontFamily.Source,
                BaseFontSize = s.BaseFontSize,
                CornerRadius = s.CornerRadius,
                Spacing = s.Spacing,
                UiScale = s.UiScale,
                Density = s.Density,
                HighContrast = s.HighContrast,
                UseMica = s.UseMica,
                AnimationsEnabled = s.AnimationsEnabled
            };
        }
    }
}
