using System.Collections.ObjectModel;
using System.Windows.Media;
using Korty.Core.Discovery;
using Korty.Core.Recovery;
using Korty.Core.Storage;

namespace Korty.App.ViewModels;

public sealed record StepRow(string Title, string Detail, string Mark, Brush Brush);
public sealed record DesignChoice(string? Id, string Name);

/// <summary>One of the two independent profiles (USB / 2.4 GHz).</summary>
public sealed class ConnectionProfileViewModel : Observable
{
    private readonly AppHost _host;
    private readonly string _key;
    private RestorePlan? _plan;
    private DesignChoice? _design;
    private bool _busy;

    public string Title { get; }
    public string Key => _key;
    public ObservableCollection<StepRow> Steps { get; } = new();
    public ObservableCollection<DesignChoice> Designs { get; } = new();
    public RelayCommand BuildPlanCommand { get; }
    public AsyncCommand ExecuteCommand { get; }

    public ConnectionProfileViewModel(AppHost host, string key, string title)
    {
        _host = host; _key = key; Title = title;
        BuildPlanCommand = new RelayCommand(BuildPlan);
        ExecuteCommand = new AsyncCommand(ExecuteAsync, () => _plan?.CanExecute == true && !_busy, ex => _host.ReportError(ex, "Восстановление"));
        ReloadDesigns();
    }

    public void ReloadDesigns()
    {
        Designs.Clear();
        Designs.Add(new DesignChoice(null, "— не назначено —"));
        foreach (var d in _host.Library.List()) Designs.Add(new DesignChoice(d.Id, d.Name));
        var id = _host.Profile.Get(_key).DesignId;
        _design = Designs.FirstOrDefault(d => d.Id == id) ?? Designs[0];
        Raise(nameof(Design));
    }

    public DesignChoice? Design
    {
        get => _design;
        set
        {
            if (!Set(ref _design, value) || value == null) return;
            _host.Profile.Get(_key).DesignId = value.Id;
            _host.SaveProfile();
            _plan = null; Steps.Clear(); ExecuteCommand.Refresh();
        }
    }

    public bool RestoreOnReconnect
    {
        get => _host.Profile.Get(_key).RestoreOnReconnect;
        set { _host.Profile.Get(_key).RestoreOnReconnect = value; _host.SaveProfile(); Raise(); }
    }

    public string RgbSummary
    {
        get { var r = _host.Profile.Get(_key).Rgb; return $"Режим {r.Mode:00} · {r.Primary.ToHex()} · яркость {r.Brightness}/5 · скорость {r.Speed}/5"; }
    }

    public string DeviceState
    {
        get
        {
            var d = _host.DeviceFor(_key);
            return d == null ? "Устройство для этого режима не обнаружено" : $"{d.Title} · {d.VidPid} · vendor-интерфейсов: {d.VendorInterfaceCount}";
        }
    }
    public Brush DeviceBrush => _host.DeviceFor(_key) != null ? Theme.Good : Theme.Muted;
    public bool HasPlan => _plan != null;
    public string PlanVerdict => _plan == null ? "" : _plan.CanExecute
        ? (_host.SimulationMode ? "План выполним (симулятор)." : "План выполним.")
        : "План заблокирован — см. причины. Это ожидаемо, пока нет аппаратно подтверждённых команд.";
    public Brush PlanBrush => _plan?.CanExecute == true ? Theme.Good : Theme.Warn;
    public string ExecuteText => _host.SimulationMode ? "Выполнить в симуляторе" : "Выполнить восстановление";

    public void BuildPlan()
    {
        try
        {
            _plan = _host.BuildRestorePlan(_key);
            Steps.Clear();
            foreach (var s in _plan.Steps)
            {
                var (mark, brush) = s.Status switch { StepStatus.Ready => ("✓", Theme.Good), StepStatus.Blocked => ("✕", Theme.Bad), _ => ("•", Theme.Muted) };
                Steps.Add(new StepRow(s.Title, s.Detail, mark, brush));
            }
            Raise(nameof(HasPlan)); Raise(nameof(PlanVerdict)); Raise(nameof(PlanBrush)); ExecuteCommand.Refresh();
        }
        catch (Exception ex) { _host.ReportError(ex, "План восстановления"); }
    }

    private async Task ExecuteAsync()
    {
        if (_plan == null) return;
        _busy = true; ExecuteCommand.Refresh();
        try
        {
            var r = await _host.Planner.ExecuteAsync(_plan, _host.DeviceFor(_key), null, CancellationToken.None);
            _host.Report(r.Message);
        }
        finally { _busy = false; ExecuteCommand.Refresh(); }
    }

    public async Task AutoRestoreAsync()
    {
        _host.Log.Info("restore", $"[{Title}] автовосстановление запрошено профилем; ждём стабилизации 3 с");
        await Task.Delay(TimeSpan.FromSeconds(3));
        await _host.RefreshNowAsync();
        BuildPlan();
        if (_plan?.CanExecute == true) { await ExecuteAsync(); }
        else _host.Report($"[{Title}] автовосстановление не выполнено: план заблокирован (см. вкладку «Профили»)");
    }

    public void RefreshState()
    {
        Raise(nameof(DeviceState)); Raise(nameof(DeviceBrush)); Raise(nameof(RgbSummary)); Raise(nameof(ExecuteText)); Raise(nameof(RestoreOnReconnect));
    }
}

public sealed class ProfilesViewModel : Observable
{
    private readonly AppHost _host;
    public ConnectionProfileViewModel Usb { get; }
    public ConnectionProfileViewModel Wireless { get; }
    public ObservableCollection<string> Events { get; } = new();

    public ProfilesViewModel(AppHost host)
    {
        _host = host;
        Usb = new ConnectionProfileViewModel(host, StudioProfile.UsbKey, "USB-C");
        Wireless = new ConnectionProfileViewModel(host, StudioProfile.WirelessKey, "2.4 GHz");
        host.TopologyUpdated += () => { Usb.RefreshState(); Wireless.RefreshState(); };
        host.ModeChanged += () => { Usb.RefreshState(); Wireless.RefreshState(); };
        host.ConnectionEvent += OnConnectionEvent;
    }

    public void Reload() { Usb.ReloadDesigns(); Wireless.ReloadDesigns(); Usb.RefreshState(); Wireless.RefreshState(); }

    private void OnConnectionEvent(ConnectionChange c)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {c.Kind}: {c.Description}");
        while (Events.Count > 50) Events.RemoveAt(Events.Count - 1);
        if (c.Kind != ConnectionChangeKind.Arrived) return;
        var vm = c.Device == ConnectionKind.UsbCable ? Usb : c.Device == ConnectionKind.Wireless24Receiver ? Wireless : null;
        if (vm is { RestoreOnReconnect: true }) _ = vm.AutoRestoreAsync();
    }
}
