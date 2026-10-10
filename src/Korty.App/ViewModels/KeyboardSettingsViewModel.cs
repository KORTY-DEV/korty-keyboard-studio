using System.Collections.ObjectModel;
using Korty.Core.Model;
using Korty.Core.Protocol;

namespace Korty.App.ViewModels;

/// <summary>One selectable key-response level card.</summary>
public sealed class LevelItem2 : Observable
{
    private bool _selected;
    public KeyResponseLevel Info { get; }
    public int Level => Info.Level;
    public string Header => $"Уровень {Info.Level} · {Info.Title}";
    public string Wired => "USB-кабель " + Info.WiredMs;
    public string Wireless => "2.4G " + Info.Wireless24Ms;
    public string Bluetooth => "Bluetooth " + Info.BluetoothMs;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public RelayCommand Choose { get; }
    public LevelItem2(KeyResponseLevel info, Action<int> pick) { Info = info; Choose = new RelayCommand(() => pick(info.Level)); }
}

/// <summary>Keyboard settings block: key response level (5 levels), sleep time, firmware key disables. Always sent as one full block.</summary>
public sealed class KeyboardSettingsViewModel : Observable
{
    private readonly AppHost _host;
    private KeyboardSettings _s;
    private string _block = "";

    public IReadOnlyList<string> SleepNames => KeyboardSettings.SleepNames;
    public ObservableCollection<LevelItem2> Levels { get; } = new();
    public AsyncCommand ApplyCommand { get; }
    public RelayCommand EnableControlCommand { get; }

    public KeyboardSettingsViewModel(AppHost host)
    {
        _host = host;
        _s = host.Profile.Settings.Keyboard.Clamped();
        foreach (var l in KeyboardSettings.Levels) Levels.Add(new LevelItem2(l, Pick));
        SyncCards();
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !HasBlock, ex => _host.ReportError(ex, "Настройки клавиатуры"));
        EnableControlCommand = new RelayCommand(() => _host.EnableControl());
        host.TopologyUpdated += Refresh; host.ModeChanged += Refresh;
        Refresh();
    }

    private void Pick(int level) => Change(_s with { KeyResponseLevel = level });

    private void Change(KeyboardSettings n)
    {
        _s = n.Clamped();
        _host.Profile.Settings.Keyboard = _s; _host.SaveProfile();
        SyncCards();
        Raise(nameof(SleepLevel)); Raise(nameof(DisableWin)); Raise(nameof(DisableAltF4)); Raise(nameof(DisableAltTab));
        Raise(nameof(Summary)); Raise(nameof(LevelNote));
    }

    private void SyncCards() { foreach (var c in Levels) c.Selected = c.Level == _s.KeyResponseLevel; }

    public int SleepLevel { get => _s.SleepLevel; set => Change(_s with { SleepLevel = value < 0 ? _s.SleepLevel : value }); }
    public bool DisableWin { get => _s.DisableWinKey; set => Change(_s with { DisableWinKey = value }); }
    public bool DisableAltF4 { get => _s.DisableAltF4; set => Change(_s with { DisableAltF4 = value }); }
    public bool DisableAltTab { get => _s.DisableAltTab; set => Change(_s with { DisableAltTab = value }); }

    public string LevelNote => _s.KeyResponseLevel switch
    {
        1 => "Минимальная задержка для игр. Возможны ложные срабатывания от дребезга контактов.",
        2 => "Быстрый отклик с небольшим запасом защиты от дребезга.",
        3 => "Заводской баланс скорости и надёжности — подходит для большинства.",
        4 => "Больше защиты от двойных нажатий, отклик чуть медленнее.",
        _ => "Максимальная защита от дребезга; задержка самая большая — для печати, не для игр.",
    };

    public string Summary =>
        $"Будет отправлено: отклик клавиш — уровень {_s.KeyResponseLevel}; сон — {KeyboardSettings.SleepNames[_s.SleepLevel]}; " +
        $"Win {(_s.DisableWinKey ? "выключена" : "работает")}, Alt+F4 {(_s.DisableAltF4 ? "выключен" : "работает")}, Alt+Tab {(_s.DisableAltTab ? "выключен" : "работает")}.";

    public string Block { get => _block; private set { if (Set(ref _block, value)) Raise(nameof(HasBlock)); } }
    public bool HasBlock => !string.IsNullOrEmpty(_block);
    public bool ShowEnable => !_host.SimulationMode && !_host.ControlEnabled;

    public void Refresh()
    {
        var pre = _host.Commands.Check(Feature04Builder.SettingsSequence(_s, _host.ProtoOptions), _host.TargetDevice);
        Block = pre.AllAllowed ? "" : "Недоступно: " + string.Join("; ", pre.DistinctBlockers);
        Raise(nameof(ShowEnable));
        ApplyCommand.Refresh();
    }

    private async Task ApplyAsync()
    {
        var r = await _host.Commands.SendAsync(Feature04Builder.SettingsSequence(_s, _host.ProtoOptions), _host.TargetDevice, null, CancellationToken.None);
        _host.Report(r.Success ? $"Настройки применены: отклик — уровень {_s.KeyResponseLevel}, сон — {KeyboardSettings.SleepNames[_s.SleepLevel]}" : r.Message);
        if (!r.Success) Block = r.Message;
    }
}
