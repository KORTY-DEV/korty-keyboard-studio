using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Korty.Core.Diagnostics;
using Korty.Core.Discovery;
using Microsoft.Win32;

namespace Korty.App.ViewModels;

public sealed record InterfaceRow(string Device, string Mi, string Usage, string Lengths, string Role, string Hint, Brush Brush);
public sealed record Hypothesis(string Id, string Title, string Evidence, string Test, string NeedsHardware, string Status);

public sealed class DiagnosticsViewModel : Observable
{
    private readonly AppHost _host;
    private DeviceSnapshot? _a, _b, _c;

    public ObservableCollection<InterfaceRow> Interfaces { get; } = new();
    public ObservableCollection<string> Comparison { get; } = new();
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public IReadOnlyList<Hypothesis> Hypotheses { get; } = BuildHypotheses();
    public RelayCommand RefreshCommand { get; }
    public AsyncCommand SnapA { get; }
    public AsyncCommand SnapB { get; }
    public AsyncCommand SnapC { get; }
    public RelayCommand CompareABCommand { get; }
    public RelayCommand CompareACCommand { get; }
    public RelayCommand SaveSnapshotsCommand { get; }
    public RelayCommand LoadSnapshotsCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand ClearViewCommand { get; }

    public DiagnosticsViewModel(AppHost host)
    {
        _host = host;
        RefreshCommand = new RelayCommand(() => _ = _host.RefreshNowAsync());
        SnapA = new AsyncCommand(() => Capture("A · только кабель (донгл вынут)", s => _a = s), onError: E);
        SnapB = new AsyncCommand(() => Capture("B · только донгл (кабель отключён)", s => _b = s), onError: E);
        SnapC = new AsyncCommand(() => Capture("C · кабель и донгл вместе", s => _c = s), onError: E);
        CompareABCommand = new RelayCommand(() => Compare(_a, _b), () => _a != null && _b != null);
        CompareACCommand = new RelayCommand(() => Compare(_a, _c), () => _a != null && _c != null);
        SaveSnapshotsCommand = new RelayCommand(SaveSnapshots, () => _a != null || _b != null || _c != null);
        LoadSnapshotsCommand = new RelayCommand(LoadSnapshots);
        ExportReportCommand = new RelayCommand(ExportReport);
        CopyLogCommand = new RelayCommand(() => { try { Clipboard.SetText(_host.Log.Export()); _host.Report("Журнал скопирован в буфер обмена"); } catch (Exception ex) { _host.ReportError(ex, "Копирование журнала"); } });
        ClearViewCommand = new RelayCommand(() => Entries.Clear());
        foreach (var e in host.Log.Snapshot()) Entries.Add(e);
        host.Log.Appended += e => Application.Current?.Dispatcher.BeginInvoke(() => { Entries.Add(e); while (Entries.Count > 1500) Entries.RemoveAt(0); });
        host.TopologyUpdated += Rebuild;
        Rebuild();
    }

    private void E(Exception ex) => _host.ReportError(ex, "Диагностика");

    public string SnapshotState =>
        $"A: {Describe(_a)}   ·   B: {Describe(_b)}   ·   C: {Describe(_c)}";
    private static string Describe(DeviceSnapshot? s) => s == null ? "—" : $"{s.Interfaces.Count} интерф. ({string.Join(", ", s.Pids)})";

    private void Rebuild()
    {
        Interfaces.Clear();
        foreach (var d in _host.Topology.Devices)
            foreach (var i in d.Interfaces)
                Interfaces.Add(new InterfaceRow(
                    $"{d.VidPid} · {d.Kind}",
                    $"MI{i.Info.InterfaceNumber:00}" + (i.Info.CollectionNumber is { } c ? $"/COL{c:00}" : ""),
                    i.Info.UsageLabel,
                    $"{i.Info.InputReportLength}/{i.Info.OutputReportLength}/{i.Info.FeatureReportLength}",
                    i.Role switch { InterfaceRole.VendorDefined => "vendor", InterfaceRole.SystemInput => "система", _ => "не прочитан" },
                    i.Hint,
                    i.Role == InterfaceRole.VendorDefined ? Theme.Info : Theme.Muted));
        Raise(nameof(Summary));
    }

    public string Summary
    {
        get
        {
            var t = _host.Topology;
            return $"Всего HID-интерфейсов в системе: {t.TotalHidInterfaces}; чужих вендоров (не открывались): {t.IgnoredOtherVendorInterfaces}; устройств 0C45: {t.Devices.Count}";
        }
    }

    private async Task Capture(string label, Action<DeviceSnapshot> store)
    {
        var raw = await Task.Run(() => _host.ScanRaw());
        var snap = DeviceSnapshot.Capture(label, raw);
        store(snap);
        _host.Report($"Снимок «{label}»: {snap.Interfaces.Count} интерфейсов, PID: {string.Join(", ", snap.Pids)}");
        Raise(nameof(SnapshotState));
        CompareABCommand.Refresh(); CompareACCommand.Refresh(); SaveSnapshotsCommand.Refresh();
    }

    private void Compare(DeviceSnapshot? x, DeviceSnapshot? y)
    {
        if (x == null || y == null) return;
        var d = DifferentialAnalyzer.Compare(x, y);
        Comparison.Clear();
        Comparison.Add($"Только в «{d.LabelA}»: {d.OnlyInA.Count} интерфейс(ов); только в «{d.LabelB}»: {d.OnlyInB.Count}; общих: {d.Common.Count}");
        foreach (var i in d.OnlyInA) Comparison.Add($"  − {i.VidPid} {i.Usage} MI{i.InterfaceNumber:00} in/out/feat={i.In}/{i.Out}/{i.Feature}");
        foreach (var i in d.OnlyInB) Comparison.Add($"  + {i.VidPid} {i.Usage} MI{i.InterfaceNumber:00} in/out/feat={i.In}/{i.Out}/{i.Feature}");
        foreach (var c in d.Conclusions) Comparison.Add("→ " + c);
    }

    private void SaveSnapshots()
    {
        var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = "korty-snapshots.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var all = new[] { _a, _b, _c }.Where(s => s != null).ToList();
            File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(all, Korty.Core.Storage.AtomicFile.Json));
            _host.Report($"Снимки сохранены ({all.Count})");
        }
        catch (Exception ex) { _host.ReportError(ex, "Сохранение снимков"); }
    }

    private void LoadSnapshots()
    {
        var dlg = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<DeviceSnapshot>>(File.ReadAllText(dlg.FileName), Korty.Core.Storage.AtomicFile.Json) ?? new();
            foreach (var s in list)
            {
                if (s.Label.StartsWith("A")) _a = s; else if (s.Label.StartsWith("B")) _b = s; else if (s.Label.StartsWith("C")) _c = s;
            }
            Raise(nameof(SnapshotState)); CompareABCommand.Refresh(); CompareACCommand.Refresh(); SaveSnapshotsCommand.Refresh();
            _host.Report($"Загружено снимков: {list.Count}");
        }
        catch (Exception ex) { _host.ReportError(ex, "Загрузка снимков"); }
    }

    private void ExportReport()
    {
        var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"korty-diagnostic-{DateTime.Now:yyyyMMdd-HHmm}.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = DiagnosticReport.Build(_host.Topology, new[] { _a, _b, _c }.Where(s => s != null)!, _host.Gate.Armed, _host.VerifiedEntryCount, _host.Log);
            File.WriteAllText(dlg.FileName, json);
            _host.Report("Отчёт сохранён (без серийных номеров и путей устройств)");
        }
        catch (Exception ex) { _host.ReportError(ex, "Экспорт отчёта"); }
    }

    private static IReadOnlyList<Hypothesis> BuildHypotheses() => new Hypothesis[]
    {
        new("H1", "Прошивка не хранит пользовательский кадр между источниками (USB ↔ 2.4 GHz) и при смене показывает пустой/чёрный экран",
            "Ваше наблюдение: экран «иногда» чёрный/сбрасывается при переключении. Источники не описывают, где хранится кадр (энергозависимо или во flash 16 МБ).",
            "Загрузить кадр по кабелю → переключить на 2.4 GHz → вернуть → отметить результат; затем выключить/включить клавиатуру.", "да (запись + наблюдение)", "не проверено"),
        new("H2", "Приёмник 2.4 GHz не отдаёт vendor-канал настройки экрана — фирменное ПО в этом режиме не может обновить экран",
            "kabelklaus: «только по USB-кабелю доступны vendor-интерфейсы». РЕАЛЬНОЕ ЧТЕНИЕ 08.10.2026: у донгла 0C45:FDFD нет интерфейсов FF68/FF67/FF13 из описаний; есть FF60/0061 (33/33 и 65/65 Б) и FFFF/0001.",
            "Дифференциальные снимки A/B на вкладке «Сравнение» (только чтение).", "нет (чтение)", "подтверждено частично: набор интерфейсов отличается"),
        new("H3", "Загрузка прервана/не завершена (нет SAVE/FINISH, неверный magic 0x064F вместо 0x0650, обрыв при re-enumeration) → экран остаётся без валидной анимации",
            "wsclx HANDOFF §6.9h: прошивка молча принимает такую загрузку, но не переключает экран на пользовательский контент. kabelklaus: потерянный ACK не прерывает отправку.",
            "Захват трафика официального ПО (нужен USB-сниффер — сторонний драйвер, отдельное разрешение владельца).", "да", "не проверено"),
        new("H4", "Объём GIF превышает ёмкость экранной памяти → декодирование не удаётся → чёрный экран",
            "wsclx: «около 30+ кадров», GET_DEVICE_INFO заявляет 255 кадров; kabelklaus по умолчанию режет до 120.",
            "Загружать GIF возрастающей длины (10/30/60/120 кадров) — по одному, наблюдая экран.", "да", "не проверено"),
        new("H5", "Гонка: приложение пишет во время переэнумерации устройства при смене режима",
            "Косвенно: ConnectionMonitor наблюдает кратковременное исчезновение интерфейсов; запись во время него обрывается.",
            "Журнал соединений (вкладка «Журнал») при переключении режима — только чтение.", "нет (чтение)", "не проверено"),
        new("H6", "Экран гаснет по таймеру (tftDisplayTime / сон) и воспринимается как «чёрный экран»",
            "wsclx: GET/SET_GAME_MODE содержит tftDisplayTime и sleepTime. Значения не документированы.",
            "Нажать клавишу/повернуть регулятор: экран «просыпается» без какой-либо записи.", "нет", "не проверено"),
    };
}
