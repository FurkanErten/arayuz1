// FlightCommands.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;

namespace arayuz_deneme_1
{
    public static class PlaneModes
    {
        // C# 8 uyumlu: hedef tipli new yerine tam tip yazıldı
        public static readonly Dictionary<string, uint> Map =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["MANUAL"] = 0,
                ["CIRCLE"] = 1,
                ["STABILIZE"] = 2,
                ["ACRO"] = 4,
                ["FBWA"] = 5,
                ["FBWB"] = 6,
                ["CRUISE"] = 7,
                ["AUTO"] = 10,
                ["RTL"] = 11,
                ["LOITER"] = 12,
                ["GUIDED"] = 15,
                ["TAKEOFF"] = 13,
            };
    }

    // EKSİK SINIF ADI → düzeltildi
    public sealed class RelayCommand : ICommand
    {
        private readonly Func<object?, bool>? _can;
        private readonly Action<object?> _run;
        public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
        public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
        public void Execute(object? p) => _run(p);
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public class FlightCommandVM : INotifyPropertyChanged
    {
        private readonly IMavlinkClient _mav;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { if (_isBusy == value) return; _isBusy = value; OnChanged(nameof(IsBusy)); Raise(); }
        }

        private bool _isArmed;
        public bool IsArmed
        {
            get => _isArmed;
            set { if (_isArmed == value) return; _isArmed = value; OnChanged(nameof(IsArmed)); }
        }

        private bool _fence;
        public bool IsFenceEnabled
        {
            get => _fence;
            set { if (_fence == value) return; _fence = value; OnChanged(nameof(IsFenceEnabled)); }
        }

        public ICommand ToggleArmCommand { get; }
        public ICommand ToggleFenceCommand { get; }
        public ICommand TakeoffCommand { get; }
        public ICommand SetModeCommand { get; }
        public ICommand KillCommand { get; }
        public ICommand RebootCommand { get; }
        public ICommand LandCommand { get; }
        public ICommand RtlCommand { get; }

        public FlightCommandVM(IMavlinkClient mav)
        {
            _mav = mav;

            ToggleArmCommand = new RelayCommand(async _ => await ToggleArmAsync(), _ => !IsBusy);
            ToggleFenceCommand = new RelayCommand(async _ => await ToggleFenceAsync(), _ => !IsBusy);
            TakeoffCommand = new RelayCommand(async alt => await TakeoffAsync(ParseAlt(alt)), _ => !IsBusy);
            SetModeCommand = new RelayCommand(async m => await SetModeAsync(m?.ToString() ?? ""), _ => !IsBusy);
            KillCommand = new RelayCommand(async _ => await KillAsync(), _ => !IsBusy);
            RebootCommand = new RelayCommand(async _ => await RebootAsync(), _ => !IsBusy);
            LandCommand = new RelayCommand(async _ => await LandAsync(), _ => !IsBusy);
            RtlCommand = new RelayCommand(async _ => await SetModeAsync("RTL"), _ => !IsBusy);

            if (TelemetryHub.Instance.TryGetLast(out var dto))
                IsArmed = dto.armed;

            TelemetryHub.Instance.OnPacket += dto => { IsArmed = dto.armed; };
        }

        private void Raise()
        {
            (ToggleArmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleFenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TakeoffCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SetModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (KillCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RebootCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LandCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RtlCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private static byte MakeBaseMode(bool armed)
        {
            const byte CUSTOM = 1;        // MAV_MODE_FLAG_CUSTOM_MODE_ENABLED
            const byte ARMED = 1 << 7;   // MAV_MODE_FLAG_SAFETY_ARMED
            return (byte)(CUSTOM | (armed ? ARMED : 0));
        }

        private static float ParseAlt(object? p)
        {
            if (p == null) return 30f;
            return float.TryParse(p.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : 30f;
        }

        // ===== Actions =====
        private async Task ToggleArmAsync()
        {
            try
            {
                IsBusy = true;
                await _mav.SetArmAsync(!IsArmed);
                // Prearm hataları varsa ACK=4 → IsArmed TelemetryHub ile gerçek zamanlı güncellenir.
            }
            finally { IsBusy = false; }
        }

        private async Task ToggleFenceAsync()
        {
            try
            {
                IsBusy = true;
                // MAV_CMD_DO_FENCE_ENABLE (207) param1: 0=disable, 1=enable
                await _mav.SendCommandLongAsync(207, p1: IsFenceEnabled ? 0f : 1f);
                IsFenceEnabled = !IsFenceEnabled;
            }
            finally { IsBusy = false; }
        }

        private async Task TakeoffAsync(float altMeters)
        {
            try
            {
                IsBusy = true;

                bool armed = IsArmed || (TelemetryHub.Instance.TryGetLast(out var dto) && dto.armed);
                byte bm = MakeBaseMode(armed);

                // GUIDED
                await _mav.SetModeAsync(PlaneModes.Map["GUIDED"], bm);
                await _mav.DoSetModeAsync(PlaneModes.Map["GUIDED"], bm);

                if (!armed)
                    await _mav.SetArmAsync(true);

                // MAV_CMD_NAV_TAKEOFF (22) — p7 hedef irtifa (Plane)
                await _mav.SendCommandLongAsync(22, p7: altMeters);
            }
            finally { IsBusy = false; }
        }

        private async Task SetModeAsync(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return;
            if (!PlaneModes.Map.TryGetValue(mode, out var cm)) return;

            try
            {
                IsBusy = true;
                bool armed = IsArmed || (TelemetryHub.Instance.TryGetLast(out var dto) && dto.armed);
                byte bm = MakeBaseMode(armed);

                // Hem #11 (SET_MODE) hem 176 (DO_SET_MODE)
                await _mav.SetModeAsync(cm, bm);
                await _mav.DoSetModeAsync(cm, bm);
            }
            finally { IsBusy = false; }
        }

        private async Task LandAsync()
        {
            try
            {
                IsBusy = true;
                // NAV_LAND (21) çoğu build’de unsupported olabilir → pratikte RTL
                await SetModeAsync("RTL");
                // Alternatif: misyon inişi kurguluysa DO_LAND_START (189)
                // await _mav.SendCommandLongAsync(189);
            }
            finally { IsBusy = false; }
        }

        private async Task KillAsync()
        {
            try { IsBusy = true; await _mav.SendCommandLongAsync(185, p1: 1f); } // FLIGHTTERMINATION
            finally { IsBusy = false; }
        }

        private async Task RebootAsync()
        {
            try { IsBusy = true; await _mav.SendCommandLongAsync(246, p1: 1f); } // PREFLIGHT_REBOOT_SHUTDOWN
            finally { IsBusy = false; }
        }
    }
}
