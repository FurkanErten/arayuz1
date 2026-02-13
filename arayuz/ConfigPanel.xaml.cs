using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Annotations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace arayuz_deneme_1
{
    // ========================== TreeView modeli ==========================
    public class LogKeyNode : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public ObservableCollection<LogKeyNode> Children { get; set; } = new();
        public bool IsGroup => Children.Any();
        public LogKeyNode? Parent { get; set; }

        bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
                if (IsGroup)
                    foreach (var ch in Children)
                        ch.IsChecked = value;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged(string? n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // Grup/leaf şablon seçici (XAML’de kullanılıyor)
    public class LogKeyTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? GroupTemplate { get; set; }
        public DataTemplate? ItemTemplate { get; set; }
        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is LogKeyNode node) return node.IsGroup ? GroupTemplate : ItemTemplate;
            return base.SelectTemplate(item, container);
        }
    }

    public partial class ConfigPanel : UserControl
    {
        // ===================== Param satırı modeli =====================
        public class ParamRow : INotifyPropertyChanged
        {
            public string Name { get; set; } = "";

            string _value = "";
            public string Value
            {
                get => _value;
                set
                {
                    if (_value == value) return;
                    _value = value;
                    OnPropertyChanged(nameof(Value));
                    IsDirty = true;
                    OnPropertyChanged(nameof(IsDirty));
                }
            }

            public string Units { get; set; } = "";
            public string Description { get; set; } = "";

            bool _isSaving;
            public bool IsSaving { get => _isSaving; set { _isSaving = value; OnPropertyChanged(nameof(IsSaving)); } }

            bool _isError;
            public bool IsError { get => _isError; set { _isError = value; OnPropertyChanged(nameof(IsError)); } }

            bool _isDirty;
            public bool IsDirty { get => _isDirty; set { _isDirty = value; OnPropertyChanged(nameof(IsDirty)); } }

            public byte ParamType { get; set; } = MAV_PARAM_TYPE_REAL32;

            public event PropertyChangedEventHandler? PropertyChanged;
            void OnPropertyChanged(string? n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        // ===================== Alanlar =====================
        private readonly Dictionary<string, UIElement> _panels;
        private readonly string _ph = "Param ara (örn: ATC_, PID)";
        private readonly Brush _muted = new SolidColorBrush(Color.FromRgb(154, 166, 188));
        private readonly ObservableCollection<ParamRow> _rows = new();
        private readonly Dictionary<string, ParamRow> _byName = new(StringComparer.OrdinalIgnoreCase);

        private const byte MAV_PARAM_TYPE_INT32 = 6;
        private const byte MAV_PARAM_TYPE_UINT32 = 7;
        private const byte MAV_PARAM_TYPE_REAL32 = 9;

        // ====== LOG ANALYSIS (BIN → CSV → Plot) ======
        private const string PythonExe = @"C:\Users\ferte\Desktop\ONEMLI\Kodlar\pycharm\.venv\Scripts\python.exe";
        private const string PyScript = @"C:\Users\ferte\Desktop\ONEMLI\Kodlar\pycharm\kodlar\bin_to_csv.py";
        private const string CsvOutputDir = @"C:\Users\ferte\source\repos\arayuz_deneme_1\arayuz_deneme_1\log\";

        private readonly Dictionary<string, List<(double t, double v)>> _logSeries =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly string _projTempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs_temp");
        private readonly List<string> _tempFiles = new();

        private readonly string _keyPh = "Seri ara (örn: ATT.Roll, GPS.Spd)";
        private List<string> _allKeys = new();
        private List<LogKeyNode> _logTree = new(); // TreeView data source
        private readonly HashSet<string> _checkedKeys = new(StringComparer.OrdinalIgnoreCase);

        // ====== PID bağlama: Tag=ParamAdı olan TextBox'ları tutar ======
        private readonly Dictionary<string, TextBox> _pidBoxes = new(StringComparer.OrdinalIgnoreCase);

        // ===================== CTOR =====================
        public ConfigPanel()
        {
            InitializeComponent();

            _panels = new Dictionary<string, UIElement>(StringComparer.OrdinalIgnoreCase)
            {
                [Norm("RC Status")] = PanelRcStatus,
                [Norm("RC Setup (Radio Cal)")] = PanelRcSetup,
                [Norm("Compass")] = PanelCompass,
                [Norm("Flight Modes")] = PanelFlightModes,
                [Norm("PID Tuning")] = PanelPid,
                [Norm("GeoFence")] = PanelFence,
                [Norm("Full Param List")] = PanelParamList,
                [Norm("Config / Tuning")] = PanelPid,
                [Norm("Battery Failsafe")] = PanelBattery,
                [Norm("Battery  Failsafe")] = PanelBattery,
                [Norm("Battery & Failsafe")] = PanelBattery,
                [Norm("Log Analysis")] = PanelLogAnalysis
            };

            ParamGrid.ItemsSource = _rows;
            ParamGrid.CellEditEnding += ParamGrid_CellEditEnding;

            ParamSearchBox.Text = _ph;
            ParamSearchBox.Foreground = _muted;
            ParamSearchBox.TextChanged += (_, __) => ApplyParamFilter();

            // Param akışını bağla
            ParamFeed.OnParam += OnParamFromBackend;
            ParamFeed.OnComplete += OnParamDownloadComplete;

            // PID kutularını keşfet
            InitPidPanelBindings();

            // Log panel hazırla
            Directory.CreateDirectory(_projTempDir);
            InitLogPanel();

            this.Unloaded += (_, __) => CleanupTemps();
            AppDomain.CurrentDomain.ProcessExit += (_, __) => CleanupTemps();
            PanelPid.Loaded += (_, __) => InitPidPanelBindings();

            ShowPanel("RC Status");
        }

        // ===================== Helpers =====================
        private static string Norm(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var collapsed = Regex.Replace(s.Trim(), @"\s+", " ");
            return collapsed.ToLowerInvariant();
        }

        private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            var p = VisualTreeHelper.GetParent(start);
            while (p is not null && p is not T) p = VisualTreeHelper.GetParent(p);
            return p as T;
        }

        private static float ParseFloatInvariant(string s)
        {
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
            if (float.TryParse(s, out f)) return f;
            throw new FormatException($"Geçersiz sayı: {s}");
        }

        private void SafeRefreshView()
        {
            var view = CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource);
            if (view == null) return;

            var ev = view as IEditableCollectionView;
            if (ev != null && (ev.IsAddingNew || ev.IsEditingItem))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var v = CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource);
                    v?.Refresh();
                }), DispatcherPriority.Background);
            }
            else
            {
                view.Refresh();
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                if (child is T t) yield return t;
                foreach (var c in FindVisualChildren<T>(child)) yield return c;
            }
        }

        // ===================== Arama/Filtre (Param) =====================
        private void ParamSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (ParamSearchBox.Text == _ph)
            {
                ParamSearchBox.Text = "";
                ParamSearchBox.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        private void ParamSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ParamSearchBox.Text))
            {
                ParamSearchBox.Text = _ph;
                ParamSearchBox.Foreground = _muted;
            }
        }

        private void ApplyParamFilter()
        {
            if (ParamGrid.ItemsSource == null) return;
            var view = CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource);
            if (view == null) return;

            var q = ParamSearchBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(q) || q == _ph)
            {
                view.Filter = null;
                view.Refresh();
                return;
            }

            view.Filter = o =>
            {
                if (o is not ParamRow r) return false;
                return (r.Name?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (r.Description?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (r.Units?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (r.Value?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
            };
            view.Refresh();
        }

        // ===================== Panel Yönetimi =====================
        private void HideAll()
        {
            foreach (var p in _panels.Values)
            {
                p.Visibility = Visibility.Collapsed;
                p.Opacity = 0;
            }
        }

        private void ShowPanel(string headerRaw)
        {
            var key = Norm(headerRaw);
            if (!_panels.TryGetValue(key, out var ui))
                ui = PanelRcStatus;

            HideAll();
            ui.Visibility = Visibility.Visible;
            StartFade(ui);

            if (ReferenceEquals(ui, PanelParamList))
            {
                StartParamDownload();
            }
            else if (ReferenceEquals(ui, PanelLogAnalysis))
            {
                EnsurePlotModel();
                CheckPythonAvailability();
            }
            else if (ReferenceEquals(ui, PanelPid))
            {
                // YENİ: kutuları tekrar tara, sonra taze değer iste
                InitPidPanelBindings();
                ParamFeed.RequestAll();
            }
        }


        private void StartFade(UIElement ui)
        {
            var grid = FindAncestor<Grid>(ui);
            if (grid != null)
            {
                var sbObj = grid.Resources["FadeIn"] ?? this.TryFindResource("FadeIn");
                if (sbObj is Storyboard sbRes)
                {
                    var sb = sbRes.Clone();
                    Storyboard.SetTarget(sb, ui);
                    sb.Begin();
                    return;
                }
            }

            var sbFallback = new Storyboard();
            var da = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(da, ui);
            Storyboard.SetTargetProperty(da, new PropertyPath(UIElement.OpacityProperty));
            sbFallback.Children.Add(da);
            sbFallback.Begin();
        }

        // ===================== Top bar (Param butonları) =====================
        private void BtnConnect_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Connect (placeholder)");
        private void BtnRead_Click(object sender, RoutedEventArgs e) => StartParamDownload();
        private void BtnWrite_Click(object sender, RoutedEventArgs e) => SaveAllDirtyAsync().Forget();
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => StartParamDownload();

        // ===================== Sol Nav seçimi =====================
        private void NavTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (NavTree.SelectedItem is TreeViewItem tvi)
                ShowPanel(tvi.Header?.ToString() ?? "");
            else
                ShowPanel(e.NewValue?.ToString() ?? "");
        }

        // ===================== RC Demo =====================
        public void UpdateRc(int ch, int microseconds)
        {
            microseconds = Math.Max(1000, Math.Min(2000, microseconds));
            switch (ch)
            {
                case 1: RcCh1.Value = microseconds; RcCh1Lbl.Text = microseconds.ToString(); break;
                case 2: RcCh2.Value = microseconds; RcCh2Lbl.Text = microseconds.ToString(); break;
                case 3: RcCh3.Value = microseconds; RcCh3Lbl.Text = microseconds.ToString(); break;
                case 4: RcCh4.Value = microseconds; RcCh4Lbl.Text = microseconds.ToString(); break;
                case 5: RcCh5.Value = microseconds; RcCh5Lbl.Text = microseconds.ToString(); break;
                case 6: RcCh6.Value = microseconds; RcCh6Lbl.Text = microseconds.ToString(); break;
                case 7: RcCh7.Value = microseconds; RcCh7Lbl.Text = microseconds.ToString(); break;
                case 8: RcCh8.Value = microseconds; RcCh8Lbl.Text = microseconds.ToString(); break;
            }
        }

        // ===================== .parm Import/Export =====================
        private const string ParmFilter = "ArduPilot Param (*.parm)|*.parm|All files (*.*)|*.*";

        private void BtnParmExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Parametreleri .parm olarak kaydet",
                    Filter = ParmFilter,
                    FileName = $"params_{DateTime.Now:yyyyMMdd_HHmmss}.parm",
                    OverwritePrompt = true
                };
                if (dlg.ShowDialog() != true) return;

                using var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
                sw.WriteLine("# ArduPilot parameter file");
                sw.WriteLine($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                foreach (var r in _rows)
                {
                    var name = (r.Name ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var valStr = (r.Value ?? "").Trim();
                    if (double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                        valStr = dv.ToString("0.########", CultureInfo.InvariantCulture);

                    sw.WriteLine($"{name},{valStr}");
                }
                sw.Flush();
                MessageBox.Show("Parametreler .parm olarak kaydedildi.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export hata: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnParmImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Kaydedilmiş .parm dosyasını seç",
                    Filter = ParmFilter,
                    Multiselect = false
                };
                if (dlg.ShowDialog() != true) return;

                var lines = File.ReadAllLines(dlg.FileName);
                int applied = 0, created = 0;
                foreach (var raw in lines)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var line = raw.Trim();
                    if (line.StartsWith("#") || line.StartsWith(";")) continue;

                    if (!TryParseParmLine(line, out var name, out var value)) continue;

                    if (_byName.TryGetValue(name, out var row))
                    {
                        row.Value = value;
                        row.IsError = false;
                        row.IsSaving = false;
                        applied++;
                    }
                    else
                    {
                        var pr = new ParamRow
                        {
                            Name = name,
                            Value = value,
                            Units = "",
                            Description = "",
                            ParamType = MAV_PARAM_TYPE_REAL32,
                            IsDirty = true
                        };
                        _rows.Add(pr);
                        _byName[name] = pr;
                        created++;
                    }
                }

                CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource)?.Refresh();

                MessageBox.Show($"İçe aktarıldı.\nGüncellenen: {applied}\nYeni eklenen: {created}\n" +
                                $"Not: Bu değerler henüz FC’ye yazılmadı. 'Write Params' ile gönder.",
                                "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Import hata: " + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static bool TryParseParmLine(string line, out string name, out string value)
        {
            name = ""; value = "";
            try
            {
                int idx = line.IndexOf(',');
                if (idx >= 0)
                {
                    name = line[..idx].Trim();
                    value = line[(idx + 1)..].Trim();
                }
                else
                {
                    var m = Regex.Match(line, @"^\s*(\S+)\s+(.+?)\s*$");
                    if (m.Success)
                    {
                        name = m.Groups[1].Value.Trim();
                        value = m.Groups[2].Value.Trim();
                    }
                }
                if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z0-9_]+$"))
                    return false;

                value = value.Replace(',', '.');

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                    value = dv.ToString("0.########", CultureInfo.InvariantCulture);

                return !string.IsNullOrWhiteSpace(value);
            }
            catch { return false; }
        }

        // ===================== Param indirme/yazma =====================
        private void StartParamDownload()
        {
            if (PanelParamList.Visibility != Visibility.Visible && PanelPid.Visibility != Visibility.Visible)
            {
                ShowPanel("Full Param List");
                return;
            }

            _rows.Clear();
            _byName.Clear();

            ParamSearchBox.Text = _ph;
            ApplyParamFilter();

            ParamFeed.RequestAll();
        }

        private void OnParamFromBackend(string name, string value, string? units, string? desc, byte? paramTypeOpt = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnParamFromBackend(name, value, units, desc, paramTypeOpt));
                return;
            }

            if (_byName.TryGetValue(name, out var row))
            {
                bool midWrite = row.IsSaving || row.IsDirty;
                row.Units = units ?? row.Units;
                row.Description = desc ?? row.Description;

                if (!midWrite)
                {
                    row.Value = value;
                    row.IsDirty = false;
                }

                if (paramTypeOpt.HasValue) row.ParamType = paramTypeOpt.Value;
                CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource)?.Refresh();
            }
            else
            {
                var pr = new ParamRow
                {
                    Name = name,
                    Value = value,
                    Units = units ?? "",
                    Description = desc ?? "",
                    ParamType = paramTypeOpt ?? MAV_PARAM_TYPE_REAL32
                };
                pr.IsDirty = false;
                _rows.Add(pr);
                _byName[name] = pr;
            }

            // PID panelindeki kutu varsa güncelle
            UpdatePidBoxIfAny(name, value);
        }

        private void OnParamDownloadComplete()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(OnParamDownloadComplete);
                return;
            }

            var view = CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource);
            if (view != null)
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(nameof(ParamRow.Name), ListSortDirection.Ascending));
                view.Refresh();
            }
        }

        private void ParamGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.Row.Item is not ParamRow row) return;
            SaveParamAsync(row).Forget();
        }

        private async Task SaveParamAsync(ParamRow row)
        {
            if (row == null || !row.IsDirty) return;

            row.IsSaving = true;
            row.IsError = false;
            CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource)?.Refresh();

            const int maxTry = 3;
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxTry; attempt++)
            {
                try
                {
                    float asFloat = ParseFloatInvariant(row.Value);
                    int? asInt = null;

                    if (row.ParamType == MAV_PARAM_TYPE_INT32 || row.ParamType == MAV_PARAM_TYPE_UINT32)
                    {
                        if (int.TryParse(row.Value, out var iv))
                        {
                            asInt = iv;
                            asFloat = iv;
                        }
                        else
                        {
                            row.ParamType = MAV_PARAM_TYPE_REAL32;
                        }
                    }

                    await ParamFeed.SendParamSetAsync(row.Name, asFloat, row.ParamType);
                    var echoed = await ParamFeed.WaitParamValueAsync(row.Name, timeoutMs: 1500);

                    if (echoed is null)
                        throw new TimeoutException("PARAM_VALUE echosu gelmedi");

                    bool ok =
                        Math.Abs(echoed.Value.Value - asFloat) <= 1e-3f ||
                        (asInt.HasValue && Math.Abs(echoed.Value.Value - asInt.Value) <= 0.5f);

                    if (!ok)
                        throw new Exception($"Echo uyuşmadı: {echoed.Value.Value} != {asFloat}");

                    row.IsDirty = false;
                    row.IsSaving = false;
                    row.IsError = false;
                    row.ParamType = echoed.Value.ParamType;
                    CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource)?.Refresh();
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    await Task.Delay(200 + attempt * 200).ConfigureAwait(false);
                }
            }

            row.IsSaving = false;
            row.IsError = true;
            CollectionViewSource.GetDefaultView(ParamGrid.ItemsSource)?.Refresh();
            MessageBox.Show($"Param yazılamadı: {row.Name}\n{lastEx?.Message}", "Write Param", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async Task SaveAllDirtyAsync()
        {
            foreach (var r in _rows)
            {
                if (r.IsDirty)
                    await SaveParamAsync(r).ConfigureAwait(false);
            }
        }

        // ===================== PID Panel: bağlama & yazma =====================
        private void InitPidPanelBindings()
        {
            _pidBoxes.Clear();
            foreach (var tb in FindVisualChildren<TextBox>(PanelPid))
            {
                if (tb.Tag is string p && !string.IsNullOrWhiteSpace(p))
                    _pidBoxes[p] = tb;
            }
        }

        private async void PidBox_Commit(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            await CommitPidTextBoxAsync(tb);
        }

        private async void PidBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && sender is TextBox tb)
            {
                e.Handled = true;
                await CommitPidTextBoxAsync(tb);
            }
        }

        private async Task CommitPidTextBoxAsync(TextBox tb)
        {
            if (tb.Tag is not string param || string.IsNullOrWhiteSpace(param)) return;

            var raw = (tb.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                // Boşsa yazmaya çalışma; görsel uyarı da verme.
                return;
            }

            // Nokta/virgül normalize
            raw = raw.Replace(',', '.');

            // Basit tip sezgisi: IMAX / THR_* / *_MAX / *_MIN gibi alanları INT32 gönder.
            bool preferInt = param.EndsWith("_IMAX", StringComparison.OrdinalIgnoreCase)
                          || param.StartsWith("THR_", StringComparison.OrdinalIgnoreCase)
                          || param.EndsWith("_MAX", StringComparison.OrdinalIgnoreCase)
                          || param.EndsWith("_MIN", StringComparison.OrdinalIgnoreCase);

            try
            {
                float asFloat = ParseFloatInvariant(raw);
                byte sendType = preferInt && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)
                                ? MAV_PARAM_TYPE_INT32
                                : MAV_PARAM_TYPE_REAL32;

                await ParamFeed.SendParamSetAsync(param, asFloat, sendType);

                // Echo bazen geç gelebilir: 1500 → 2200ms
                var echoed = await ParamFeed.WaitParamValueAsync(param, timeoutMs: 2200);
                if (echoed is null)
                {
                    MarkTb(tb, ok: false, $"Echo yok: {param}");
                    return;
                }

                // Kıyas toleransı: float için 1e-3, int için ±0.5
                bool ok = Math.Abs(echoed.Value.Value - asFloat) <= 1e-3f
                       || (sendType == MAV_PARAM_TYPE_INT32 && Math.Abs(echoed.Value.Value - (int)Math.Round(asFloat)) <= 0.5f);

                MarkTb(tb, ok, ok ? null : $"Uyumsuz echo: {echoed.Value.Value}");
            }
            catch (Exception ex)
            {
                MarkTb(tb, ok: false, ex.Message);
            }
        }


        private void UpdatePidBoxIfAny(string name, string value)
        {
            if (_pidBoxes.TryGetValue(name, out var tb))
            {
                if (!tb.IsKeyboardFocused)
                    tb.Text = value;
            }
        }


        private void MarkTb(TextBox tb, bool ok, string? tooltip)
        {
            tb.ToolTip = tooltip;
            var good = (SolidColorBrush)new BrushConverter().ConvertFrom("#12351F")!;
            var bad = (SolidColorBrush)new BrushConverter().ConvertFrom("#3A1420")!;
            var old = tb.BorderBrush;
            tb.BorderBrush = ok ? good : bad;

            DispatcherTimer t = new() { Interval = TimeSpan.FromSeconds(1.0) };
            t.Tick += (_, __) => { tb.BorderBrush = old; t.Stop(); };
            t.Start();
        }

        // ===================== LOG ANALYSIS =====================
        private void InitLogPanel()
        {
            EnsurePlotModel();

            if (KeySearchBox != null && string.IsNullOrWhiteSpace(KeySearchBox.Text))
            {
                KeySearchBox.Text = _keyPh;
                KeySearchBox.Foreground = _muted;
            }

            CheckPythonAvailability();
        }

        private void EnsurePlotModel()
        {
            if (Plot != null && Plot.Model == null)
                Plot.Model = CreatePlotModel();
        }

        private PlotModel CreatePlotModel()
        {
            var m = new PlotModel { Title = "Telemetry Plot" };
            m.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Time (s)" });
            m.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Value" });
            return m;
        }

        private void CheckPythonAvailability()
        {
            if (LblInfo == null) return;

            var sb = new StringBuilder();
            if (!File.Exists(PythonExe))
                sb.AppendLine("Uyarı: Python yolu bulunamadı. (PythonExe sabit yolunu kontrol et)");
            if (!File.Exists(PyScript))
                sb.AppendLine("Uyarı: bin_to_csv.py bulunamadı. (PyScript sabit yolunu kontrol et)");
            if (sb.Length > 0) LblInfo.Text = sb.ToString().Trim();
        }

        private async void BtnPickBin_Click(object? sender, RoutedEventArgs e)
        {
            CleanupTemps();

            var dlg = new OpenFileDialog { Filter = "DataFlash BIN|*.bin;*.BIN|All|*.*" };
            if (dlg.ShowDialog() != true) return;

            if (TxtBin != null) TxtBin.Text = dlg.FileName;

            Directory.CreateDirectory(CsvOutputDir);

            var csvPath = Path.Combine(
                CsvOutputDir,
                $"{Path.GetFileNameWithoutExtension(dlg.FileName)}.csv"
            );

            try { if (File.Exists(csvPath)) File.Delete(csvPath); } catch { }

            if (!_tempFiles.Contains(csvPath)) _tempFiles.Add(csvPath);

            await ConvertAndLoadAsync(dlg.FileName, csvPath);
        }

        private async Task ConvertAndLoadAsync(string binPath, string csvPath)
        {
            if (!File.Exists(PythonExe))
            {
                MessageBox.Show($"Python bulunamadı:\n{PythonExe}");
                return;
            }
            if (!File.Exists(PyScript))
            {
                MessageBox.Show($"Dönüştürücü script bulunamadı:\n{PyScript}");
                return;
            }

            BtnPickBin.IsEnabled = false;
            Prg.Visibility = Visibility.Visible;
            LblInfo.Text = "Dönüştürülüyor (Python)…";

            try
            {
                var ok = await RunPythonArgsAsync(PythonExe, PyScript,
                    $"\"{binPath}\" --output \"{csvPath}\"");

                if (!ok)
                {
                    ok = await RunPythonRedirectAsync(PythonExe, PyScript, binPath, csvPath);
                }

                if (!ok || !File.Exists(csvPath))
                {
                    MessageBox.Show("Dönüşüm başarısız. Detay için alttaki loga bak.");
                    return;
                }

                LblInfo.Text = "Dönüşüm tamam. CSV yükleniyor…";
                await LoadCsvAsync(csvPath);
            }
            finally
            {
                Prg.Visibility = Visibility.Collapsed;
                BtnPickBin.IsEnabled = true;
            }
        }

        private async Task<bool> RunPythonArgsAsync(string pythonExe, string scriptPath, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{scriptPath}\" {args}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = new Process { StartInfo = psi };
                p.Start();
                string stdOut = await p.StandardOutput.ReadToEndAsync();
                string stdErr = await p.StandardError.ReadToEndAsync();
                await Task.Run(() => p.WaitForExit());

                var log = (stdOut + (string.IsNullOrWhiteSpace(stdErr) ? "" : "\nERR: " + stdErr)).Trim();
                if (!string.IsNullOrWhiteSpace(log)) LblInfo.Text = log;

                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                LblInfo.Text = "Python (args) çalıştırılamadı: " + ex.Message;
                return false;
            }
        }

        private async Task<bool> RunPythonRedirectAsync(string pythonExe, string scriptPath, string binPath, string csvPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{pythonExe}\" \"{scriptPath}\" \"{binPath}\" > \"{csvPath}\"\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = new Process { StartInfo = psi };
                p.Start();
                string so = await p.StandardOutput.ReadToEndAsync();
                string se = await p.StandardError.ReadToEndAsync();
                await Task.Run(() => p.WaitForExit());

                var log = (so + (string.IsNullOrWhiteSpace(se) ? "" : "\nERR: " + se)).Trim();
                if (!string.IsNullOrWhiteSpace(log)) LblInfo.Text = log;

                return p.ExitCode == 0 && File.Exists(csvPath);
            }
            catch (Exception ex)
            {
                LblInfo.Text = "Python (redirect) hatası: " + ex.Message;
                return false;
            }
        }

        private async Task LoadCsvAsync(string path)
        {
            if (!File.Exists(path)) { MessageBox.Show("CSV bulunamadı."); return; }

            _logSeries.Clear();
            _allKeys.Clear();
            _logTree.Clear();
            if (LstKeys != null) LstKeys.ItemsSource = null;

            var ci = CultureInfo.InvariantCulture;

            await Task.Run(() =>
            {
                using var sr = new StreamReader(path);
                string? line = sr.ReadLine(); // header
                while ((line = sr.ReadLine()) != null)
                {
                    var sp = line.Split(',');
                    if (sp.Length < 3) continue;
                    var key = sp[0];

                    if (!double.TryParse(sp[1], NumberStyles.Float, ci, out double t)) continue;
                    if (!double.TryParse(sp[2], NumberStyles.Float, ci, out double v)) continue;

                    if (!_logSeries.TryGetValue(key, out var list))
                    {
                        list = new List<(double t, double v)>();
                        _logSeries[key] = list;
                    }
                    list.Add((t, v));
                }
            });

            _allKeys = _logSeries.Keys.OrderBy(k => k).ToList();
            PopulateLogTree(_allKeys);

            if (KeySearchBox != null && string.IsNullOrWhiteSpace(KeySearchBox.Text))
            {
                KeySearchBox.Text = _keyPh;
                KeySearchBox.Foreground = _muted;
            }

            LblInfo.Text = $"CSV yüklendi: {_logSeries.Count} seri";
        }

        private void PopulateLogTree(IEnumerable<string> keys)
        {
            var newTree = new List<LogKeyNode>();
            var groups = new Dictionary<string, LogKeyNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                var parts = key.Split('.');
                string groupName = parts.Length > 1 ? parts[0] : "Diğer";
                string itemName = parts.Length > 1 ? string.Join(".", parts.Skip(1)) : key;

                if (!groups.TryGetValue(groupName, out var groupNode))
                {
                    groupNode = new LogKeyNode { Name = groupName, FullPath = groupName };
                    AttachNodeEvents(groupNode);
                    groups[groupName] = groupNode;
                    newTree.Add(groupNode);
                }

                var leaf = new LogKeyNode
                {
                    Name = itemName,
                    FullPath = key,
                    Parent = groups[groupName],
                    IsChecked = _checkedKeys.Contains(key)
                };
                AttachNodeEvents(leaf);
                groups[groupName].Children.Add(leaf);
            }

            _logTree = newTree.OrderBy(g => g.Name).ToList();
            if (LstKeys != null) LstKeys.ItemsSource = _logTree;
        }

        private void AttachNodeEvents(LogKeyNode node)
        {
            node.PropertyChanged -= OnNodePropertyChanged;
            node.PropertyChanged += OnNodePropertyChanged;
        }

        private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LogKeyNode.IsChecked)) return;
            if (sender is not LogKeyNode n) return;

            if (!n.IsGroup)
            {
                if (n.IsChecked) _checkedKeys.Add(n.FullPath);
                else _checkedKeys.Remove(n.FullPath);
            }
        }

        private void BtnClearSel_Click(object sender, RoutedEventArgs e)
        {
            _checkedKeys.Clear();
            foreach (var root in _logTree) SetCheckedRecursive(root, false);

            if (LstKeys != null)
            {
                foreach (var item in _logTree)
                    if (LstKeys.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                        tvi.IsExpanded = false;
            }
        }

        private static void SetCheckedRecursive(LogKeyNode n, bool val)
        {
            n.IsChecked = val;
            foreach (var ch in n.Children) SetCheckedRecursive(ch, val);
        }

        private void BtnPlot_Click(object sender, RoutedEventArgs e)
        {
            if (LstKeys == null || Plot == null)
            {
                MessageBox.Show("UI öğeleri bulunamadı (LstKeys/Plot).");
                return;
            }

            if (_checkedKeys.Count == 0)
            {
                MessageBox.Show("Lütfen çizmek için 1+ seri işaretleyin (checkbox).");
                return;
            }

            var model = CreatePlotModel();

            foreach (var key in _checkedKeys.OrderBy(k => k))
            {
                if (!_logSeries.TryGetValue(key, out var list)) continue;
                list.Sort((a, b) => a.t.CompareTo(b.t));

                var s = new LineSeries { Title = key, StrokeThickness = 1.2 };
                foreach (var (t, v) in list)
                    s.Points.Add(new DataPoint(t, v));

                model.Series.Add(s);
            }

            model.IsLegendVisible = model.Series.Count > 1;
            Plot.Model = model;
        }

        private void KeySearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (KeySearchBox.Text == _keyPh)
            {
                KeySearchBox.Text = "";
                KeySearchBox.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        private void KeySearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(KeySearchBox.Text))
            {
                KeySearchBox.Text = _keyPh;
                KeySearchBox.Foreground = _muted;
            }
        }

        private void KeySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (LstKeys == null || _allKeys == null) return;
            var q = KeySearchBox.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(q) || q == _keyPh)
            {
                PopulateLogTree(_allKeys);
                return;
            }

            var filteredKeys = _allKeys.Where(k => k.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            PopulateLogTree(filteredKeys);

            if (LstKeys != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var item in _logTree)
                        if (LstKeys.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                            tvi.IsExpanded = true;
                }), DispatcherPriority.ContextIdle);
            }
        }

        private void CleanupTemps()
        {
            try
            {
                if (Directory.Exists(CsvOutputDir))
                {
                    foreach (var f in Directory.EnumerateFiles(CsvOutputDir, "*.csv"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }

                foreach (var f in _tempFiles.ToArray())
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    _tempFiles.Remove(f);
                }

                if (Directory.Exists(_projTempDir))
                {
                    foreach (var f in Directory.EnumerateFiles(_projTempDir, "*.csv"))
                    {
                        try { File.Delete(f); } catch { }
                    }

                    if (!Directory.EnumerateFileSystemEntries(_projTempDir).Any())
                    {
                        try { Directory.Delete(_projTempDir, false); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    internal static class TaskExt
    {
        public static void Forget(this Task t) { /* fire-and-forget */ }
    }
}
