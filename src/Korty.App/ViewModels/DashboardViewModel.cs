using Korty.Core.Protocol;
using System.Collections.ObjectModel;
using System.Windows.Media;
using Korty.Core.Discovery;
using Korty.Core.Storage;

namespace Korty.App.ViewModels;

public sealed record DeviceCard(string Title, string VidPid, string Summary, string Notes, Brush Brush);

public sealed class DashboardViewModel : Observable
{
    private readonly AppHost _host;
    public ObservableCollection<DeviceCard> Devices { get; } = new();
    public ObservableCollection<CapabilityRow> Capabilities { get; } = new();
    public RelayCommand RefreshCommand { get; }

    public DashboardViewModel(AppHost host)
    {
        _host = host;
        RefreshCommand = new RelayCommand(() => _ = _host.RefreshNowAsync());
        host.TopologyUpdated += Rebuild;
        host.ModeChanged += Rebuild;
        host.TargetChanged += Rebuild;
        Rebuild();
    }

    public bool UsbTarget { get => _host.TargetKey == StudioProfile.UsbKey; set { if (value) _host.SetTarget(StudioProfile.UsbKey); } }
    public bool WirelessTarget { get => _host.TargetKey == StudioProfile.WirelessKey; set { if (value) _host.SetTarget(StudioProfile.WirelessKey); } }

    // KeyboardView bindings ------------------------------------------------
    public Color KeyColor { get; private set; }
    public int Brightness { get; private set; }
    public bool Active { get; private set; }
    public ImageSource? ScreenImage { get; private set; }
    public bool ScreenBlack { get; private set; }
    public string ScreenCaption { get; private set; } = "";
    public string Headline { get; private set; } = "";
    public string SubHeadline { get; private set; } = "";
    public Brush HeadlineBrush { get; private set; } = Theme.Muted;

    private void Rebuild()
    {
        var t = _host.Topology;
        Devices.Clear();
        foreach (var d in t.Devices)
        {
            var brush = d.Kind switch { ConnectionKind.UsbCable or ConnectionKind.Wireless24Receiver => Theme.Good, ConnectionKind.Bootloader => Theme.Bad, _ => Theme.Muted };
            var sys = d.Interfaces.Count(i => i.Role == InterfaceRole.SystemInput);
            Devices.Add(new DeviceCard(d.Title, d.VidPid,
                $"{d.Interfaces.Count} HID-интерфейсов: {sys} системных, {d.VendorInterfaceCount} vendor-defined",
                string.Join("\n", d.Notes), brush));
        }

        var key = _host.TargetKey;
        var dev = _host.DeviceFor(key);
        Active = dev != null && dev.IsActionable;
        var rgb = _host.Profile.Get(key).Rgb;
        KeyColor = Color.FromRgb(rgb.Primary.R, rgb.Primary.G, rgb.Primary.B);
        Brightness = rgb.Brightness;

        ScreenBlack = false; ScreenImage = null; ScreenCaption = "TFT 128×128";
        if (_host.SimulationMode && _host.Sim.Screen != null && Active)
        {
            ScreenImage = ImageUtil.FromBgra(_host.Sim.Screen.Frames[0].Bgra);
            ScreenCaption = "на экране симулятора";
        }
        else if (_host.SimulationMode && _host.Sim.ScreenIsBlack && Active)
        {
            ScreenBlack = true; ScreenCaption = "экран симулятора: чёрный";
        }
        else if (_host.Profile.Get(key).DesignId is { } id && LibraryPaths.IsValidId(id) &&
                 ImageUtil.LoadFile(_host.Library.ThumbnailPath(id)) is { } thumb)
        {
            ScreenImage = thumb; ScreenCaption = "локальный предпросмотр";
        }

        if (t.Usb == null && t.Wireless == null)
        {
            Headline = "Клавиатура не обнаружена";
            SubHeadline = _host.SimulationMode
                ? "Симулятор отключён. Выберите подключение на странице «Симулятор»."
                : "Подключите USB-C или приёмник 2.4 GHz. Приложение только читает дескрипторы и ничего не отправляет.";
            HeadlineBrush = Theme.Muted;
        }
        else
        {
            var names = new List<string>();
            if (t.Usb != null) names.Add("USB-C");
            if (t.Wireless != null) names.Add("2.4 GHz");
            Headline = string.Join(" + ", names);
            SubHeadline = t.Usb != null ? "AK820 Pro на кабеле: управление подсветкой и экраном доступно (после включения записи в «Безопасности»)." : "Только приёмник 2.4 GHz: он передаёт ввод, но экран и подсветку по воздуху настроить нельзя — подключите USB-C.";
            HeadlineBrush = Theme.Good;
        }

        BuildCapabilities();
        foreach (var p in new[] { nameof(KeyColor), nameof(Brightness), nameof(Active), nameof(ScreenImage), nameof(ScreenBlack), nameof(ScreenCaption),
                     nameof(Headline), nameof(SubHeadline), nameof(HeadlineBrush) }) Raise(p);
    }

    private string StageState(string stage, out Brush brush)
    {
        if (_host.SimulationMode) { brush = Theme.Info; return "Симулятор"; }
        if (!_host.ControlEnabled) { brush = Theme.Muted; return "Выключено · «Безопасность»"; }
        brush = Theme.Good;
        return "Готово";
    }

    private void BuildCapabilities()
    {
        Capabilities.Clear();
        void Add(string n, string s, string d, Brush b) => Capabilities.Add(new CapabilityRow(n, s, d, b));
        Add("Подсветка RGB", StageState(Stages.Rgb, out var b1), "Работает по USB-C (подтверждено на вашей клавиатуре). Любой цвет, 20 режимов, яркость, скорость. Хранится в клавиатуре.", b1);
        Add("Картинка PNG на экран", StageState(Stages.Png, out var b2), "Работает по USB-C. Один кадр 128×128, хранится в клавиатуре.", b2);
        Add("Анимация GIF на экран", StageState(Stages.Gif, out var b3), "Работает по USB-C. Хранится в клавиатуре; начинайте с коротких анимаций (ёмкость не измерена).", b3);
        Add("Возврат вашего экрана", "Fn + Del на клавиатуре", "После отключения питания клавиатура показывает заводской экран, а загруженная GIF остаётся в памяти. Программной команды, равной Fn+Del, нет в известном протоколе — и приложение не пробует неизвестные команды наугад.", Theme.Warn);
        Add("Настройки клавиатуры", StageState(Stages.Settings, out var b4), "Время отклика клавиш (5 уровней), сон, отключение Win / Alt+F4 / Alt+Tab. Формат взят из фирменной программы; на вашей клавиатуре ещё не проверялся.", b4);
        Add("Часы / заряд / подключение поверх GIF", "Невозможно", "Штатная прошивка не даёт такой функции: нет команды наложения, API батареи и типа подключения. Допустим «снимок времени» внутри картинки — он статичен и не идёт.", Theme.Muted);
        Add("2.4 GHz", "Только ввод", "Приёмник не имеет управляющих интерфейсов FF13/FF68: экран и подсветка по воздуху не настраиваются.", Theme.Muted);
        Add("Частота опроса", "Недоступно", "Команда не найдена ни в одном известном протоколе; неизвестные команды приложение не отправляет.", Theme.Muted);
        Add("Обнаружение и диагностика", "Работает · только чтение", "Дескрипторы читаются без отправки команд; чужие устройства не открываются.", Theme.Good);
    }
}
