using System.Collections.ObjectModel;
using System.Windows.Media;
using Korty.Core.Discovery;
using Korty.Core.Storage;

namespace Korty.App.ViewModels;

public sealed record NavItem(string Title, string Glyph, Observable Page);

public sealed class MainViewModel : Observable
{
    public AppHost Host { get; }
    public IReadOnlyList<NavItem> Nav { get; }
    private NavItem _selected;
    private string _status = "Готово. Приложение ничего не отправляет в клавиатуру автоматически.";
    private string _connection = "Поиск…";
    private Brush _connectionBrush = Theme.Muted;

    public DashboardViewModel Dashboard { get; }
    public ScreenEditorViewModel Screen { get; }
    public RgbViewModel Rgb { get; }
    public ProfilesViewModel Profiles { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public SimulatorViewModel Simulator { get; }
    public SafetyViewModel Safety { get; }
    public KeyboardSettingsViewModel KeyboardSettings { get; }

    public MainViewModel(AppHost host)
    {
        Host = host;
        Dashboard = new DashboardViewModel(host);
        Screen = new ScreenEditorViewModel(host);
        Rgb = new RgbViewModel(host);
        Profiles = new ProfilesViewModel(host);
        Diagnostics = new DiagnosticsViewModel(host);
        Simulator = new SimulatorViewModel(host);
        Safety = new SafetyViewModel(host);
        KeyboardSettings = new KeyboardSettingsViewModel(host);
        Nav = new NavItem[]
        {
            new("Обзор", "◧", Dashboard),
            new("Экран TFT", "▣", Screen),
            new("Подсветка", "✦", Rgb),
            new("Клавиатура", "⌨", KeyboardSettings),
            new("Профили", "☰", Profiles),
            new("Диагностика", "⌕", Diagnostics),
            new("Симулятор", "⬡", Simulator),
            new("Безопасность", "⛨", Safety),
        };
        _selected = Nav[0];
        host.Status += m => Status = $"{DateTime.Now:HH:mm:ss}  {m}";
        host.TopologyUpdated += Refresh;
        host.ModeChanged += Refresh;
        Refresh();
    }

    public NavItem Selected { get => _selected; set { if (Set(ref _selected, value)) Raise(nameof(Page)); } }
    public Observable Page => _selected.Page;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Connection { get => _connection; private set => Set(ref _connection, value); }
    public Brush ConnectionBrush { get => _connectionBrush; private set => Set(ref _connectionBrush, value); }
    public bool Simulation => Host.SimulationMode;
    public string ModeBadge => Host.SimulationMode ? "СИМУЛЯЦИЯ" : Host.ControlEnabled ? "УПРАВЛЕНИЕ ВКЛЮЧЕНО" : "ТОЛЬКО ЧТЕНИЕ";
    public Brush ModeBrush => Host.SimulationMode ? Theme.Info : Host.ControlEnabled ? Theme.Good : Theme.Muted;

    private void Refresh()
    {
        var t = Host.Topology;
        var usb = t.Usb; var wl = t.Wireless;
        if (usb != null && wl != null) { Connection = "USB-C + приёмник 2.4 GHz"; ConnectionBrush = Theme.Good; }
        else if (usb != null) { Connection = "USB-C"; ConnectionBrush = Theme.Good; }
        else if (wl != null) { Connection = "2.4 GHz (только ввод)"; ConnectionBrush = Theme.Good; }
        else if (t.HasBootloader) { Connection = "Загрузчик ISP — не трогать"; ConnectionBrush = Theme.Bad; }
        else { Connection = "Клавиатура не обнаружена"; ConnectionBrush = Theme.Muted; }
        Raise(nameof(ModeBadge)); Raise(nameof(ModeBrush)); Raise(nameof(Simulation));
    }
}
