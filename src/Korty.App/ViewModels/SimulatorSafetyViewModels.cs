using System.Collections.ObjectModel;
using System.Windows.Media;
using Korty.Core.Protocol;
using Korty.Core.Safety;
using Korty.Core.Simulation;

namespace Korty.App.ViewModels;

public sealed class SimulatorViewModel : Observable
{
    private readonly AppHost _host;
    private string _failAfter = "";
    public ObservableCollection<string> Journal { get; } = new();
    public RelayCommand EnableCommand { get; }
    public RelayCommand BlankScreenCommand { get; }

    public SimulatorViewModel(AppHost host)
    {
        _host = host;
        EnableCommand = new RelayCommand(() => _host.SetSimulation(true), () => !_host.SimulationMode);
        BlankScreenCommand = new RelayCommand(() => _host.Sim.ClearScreenToBlack(), () => _host.SimulationMode);
        host.Sim.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
        host.ModeChanged += () => { Refresh(); EnableCommand.Refresh(); BlankScreenCommand.Refresh(); };
        Refresh();
    }

    public bool Enabled => _host.SimulationMode;
    public bool Disabled => !_host.SimulationMode;

    private void SetConn(SimConnection c) { if (_host.SimulationMode) _host.Sim.SetConnection(c); }
    public bool ConnNone { get => _host.Sim.Connection == SimConnection.None; set { if (value) SetConn(SimConnection.None); } }
    public bool ConnUsb { get => _host.Sim.Connection == SimConnection.Usb; set { if (value) SetConn(SimConnection.Usb); } }
    public bool ConnWireless { get => _host.Sim.Connection == SimConnection.Wireless24; set { if (value) SetConn(SimConnection.Wireless24); } }
    public bool ConnBoth { get => _host.Sim.Connection == SimConnection.UsbAndReceiver; set { if (value) SetConn(SimConnection.UsbAndReceiver); } }

    public bool WirelessControl { get => _host.Sim.WirelessControlAvailable; set { _host.Sim.WirelessControlAvailable = value; _host.Sim.NotifyChanged(); Raise(); } }
    public bool ResetOnSwitch { get => _host.Sim.ResetScreenOnSwitch; set { _host.Sim.ResetScreenOnSwitch = value; Raise(); } }
    public bool Decoys { get => _host.Sim.IncludeDecoys; set { _host.Sim.IncludeDecoys = value; Raise(); _host.Sim.NotifyChanged(); } }
    public string FailAfter
    {
        get => _failAfter;
        set { _failAfter = value; _host.Sim.FailAfterWrites = int.TryParse(value, out var n) && n >= 0 ? n : -1; Raise(); }
    }

    public string State
    {
        get
        {
            var s = _host.Sim;
            var rgb = s.Rgb == null ? "подсветка: не задана" : $"подсветка: режим {s.Rgb.Mode}, {s.Rgb.Primary.ToHex()}, яркость {s.Rgb.Brightness}";
            var scr = s.ScreenIsBlack ? "экран: ЧЁРНЫЙ" : s.Screen == null ? "экран: заводской" : $"экран: {s.Screen.Frames.Count} кадр(ов), {s.Screen.TotalDurationMs} мс";
            return $"{rgb}\n{scr}\nпринято записей: {s.AcceptedWrites} · семейство: {s.Family}";
        }
    }

    public void Refresh()
    {
        Journal.Clear();
        foreach (var j in _host.Sim.Journal.TakeLast(60).Reverse()) Journal.Add(j);
        foreach (var p in new[] { nameof(Enabled), nameof(Disabled), nameof(ConnNone), nameof(ConnUsb), nameof(ConnWireless), nameof(ConnBoth), nameof(WirelessControl), nameof(ResetOnSwitch), nameof(State) }) Raise(p);
    }
}

/// <summary>One activation stage (RGB / PNG / GIF) as shown on the Safety page.</summary>
public sealed class StageCardViewModel : Observable
{
    private readonly AppHost _host;
    private readonly Func<string, bool> _confirm;
    public StageInfo Info { get; }
    public RelayCommand ActionCommand { get; }

    public StageCardViewModel(AppHost host, StageInfo info, Func<string, bool> confirm)
    {
        _host = host; Info = info; _confirm = confirm;
        ActionCommand = new RelayCommand(Act, () => IsActive || CannotReason == null);
    }

    public bool IsActive => _host.ActiveStages.Contains(Info.Id);
    public string? CannotReason => IsActive ? null : ActivationStages.CannotActivate(Info.Id, _host.Gate.VerifiedRegistry);
    public string StateText => IsActive ? "включён" : CannotReason != null ? "не активирован · " + CannotReason : "не активирован";
    public Brush StateBrush => IsActive ? Theme.Good : Theme.Muted;
    public string ActionText => IsActive ? "Отключить" : "Включить этап";

    public void Refresh()
    {
        foreach (var p in new[] { nameof(IsActive), nameof(CannotReason), nameof(StateText), nameof(StateBrush), nameof(ActionText) }) Raise(p);
        ActionCommand.Refresh();
    }

    private void Act()
    {
        try
        {
            if (IsActive) { _host.DeactivateStage(Info.Id); _host.Report($"{Info.Title}: отключён (зависимые этапы тоже)"); return; }
            var text = $"{Info.Title}\n\nЧто разрешается: {Info.Allows}\n\nВключение только записывает разрешение; само по себе ничего не отправляет.\n\nВключить этап?";
            if (!_confirm(text)) return;
            _host.ActivateStage(Info.Id);
            _host.Report($"{Info.Title}: включён");
        }
        catch (Exception ex) { _host.ReportError(ex, "Активация этапа"); }
    }
}

public sealed record CatalogRow(string Family, string Id, string Name, string Risk, string Evidence, string Source, string Notes, Brush Brush);

public sealed class SafetyViewModel : Observable
{
    private readonly AppHost _host;
    private bool _advanced;
    public IReadOnlyList<CatalogRow> Catalog { get; }
    public IReadOnlyList<ProtocolFamily> Families { get; } = Enum.GetValues<ProtocolFamily>();
    public ObservableCollection<string> Problems { get; } = new();
    public ObservableCollection<StageCardViewModel> Stages { get; } = new();
    public RelayCommand ToggleControlCommand { get; }

    public SafetyViewModel(AppHost host)
    {
        _host = host;
        Catalog = CommandCatalog.All.Select(c => new CatalogRow(c.Family.ToString(), $"0x{c.Id:X2}", c.Name, c.Risk.ToString(), c.Evidence.ToString(), c.Source, c.Notes,
            c.NeverSendable ? Theme.Bad : c.Risk == CommandRisk.PersistentSetting ? Theme.Warn : Theme.Muted)).ToList();
        ToggleControlCommand = new RelayCommand(() => { if (_host.ControlEnabled) _host.DisableControl(); else _host.EnableControl(); Refresh(); });
        foreach (var p in host.Gate.VerifiedRegistry.LoadProblems) Problems.Add(p);
        foreach (var info in ActivationStages.Infos)
            Stages.Add(new StageCardViewModel(host, info, text => System.Windows.MessageBox.Show(text, "Этап записи",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes));
        host.ModeChanged += Refresh;
        Refresh();
    }

    public bool Simulation { get => _host.SimulationMode; set { if (value != _host.SimulationMode) _host.SetSimulation(value); Refresh(); } }
    public ProtocolFamily Family { get => _host.Family; set { if (value != _host.Family) _host.SetFamily(value); Raise(); } }
    public bool ShowAdvanced { get => _advanced; set => Set(ref _advanced, value); }

    public bool SaveFlag { get => _host.Profile.Settings.SaveFlag == 1; set { _host.Profile.Settings.SaveFlag = value ? 1 : 0; _host.SaveProfile(); _host.Log.Info("proto", $"SAVE byte8 = 0x0{(value ? 1 : 0)}"); Raise(); } }
    public bool FinishAfterLighting { get => _host.Profile.Settings.FinishAfterLighting; set { _host.Profile.Settings.FinishAfterLighting = value; _host.SaveProfile(); Raise(); } }
    public bool GohvImage { get => _host.Profile.Settings.GohvImage; set { _host.Profile.Settings.GohvImage = value; _host.SaveProfile(); Raise(); } }

    public string Verdict => _host.SimulationMode ? "Режим симуляции — на клавиатуру ничего не отправляется"
        : _host.ControlEnabled ? "Управление клавиатурой включено" : "Управление клавиатурой выключено — программа только читает";
    public Brush VerdictBrush => _host.SimulationMode ? Theme.Info : _host.ControlEnabled ? Theme.Good : Theme.Muted;
    public string Explain => _host.ControlEnabled
        ? "Программа может менять подсветку, экран и часы. Команды проверяются перед каждой отправкой; сброс, перепрошивка и неизвестные команды заблокированы навсегда."
        : "Чтобы менять подсветку и экран, включите управление — один раз подтвердите в окне. Выключено: программа только читает список устройств и ничего не отправляет.";
    public string ToggleText => _host.ControlEnabled ? "Выключить управление" : "Включить управление клавиатурой";
    public string RegistryText => $"Разрешено команд: {_host.VerifiedEntryCount} · файл: {_host.VerifiedRegistryDisplayPath}";
    public string Checklist =>
        "Перед каждой отправкой проверяется:\n" +
        "• включено управление и этап (RGB / PNG / GIF / часы) для этой команды;\n" +
        "• устройство — AK820 Pro (VID 0C45, PID 8009, не загрузчик), интерфейс управления FF13 / данных FF68;\n" +
        "• длина отчёта точно совпадает с дескриптором; команда не из заблокированных (сброс, ремап, макросы, игровой режим).";

    public void Refresh()
    {
        foreach (var c in Stages) c.Refresh();
        foreach (var p in new[] { nameof(Simulation), nameof(Family), nameof(Verdict), nameof(VerdictBrush), nameof(Explain), nameof(ToggleText), nameof(RegistryText), nameof(Checklist), nameof(SaveFlag), nameof(FinishAfterLighting), nameof(GohvImage) }) Raise(p);
    }
}
