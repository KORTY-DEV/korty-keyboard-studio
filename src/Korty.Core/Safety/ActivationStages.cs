using System.Text.Json;
using Korty.Core.Hid;
using Korty.Core.Protocol;
using Korty.Core.Storage;

namespace Korty.Core.Safety;

/// <summary>Static description of one activation stage shown to the owner.</summary>
public sealed record StageInfo(string Id, string Title, string Summary, string Allows, string Requires, string Prerequisite);

/// <summary>
/// Owner-driven, stage-by-stage activation of real writes for the keyboard whose behaviour was confirmed in the reference
/// web configurator (USB, 0C45:8009, control = FF13 feature 65, data = FF68 output 4097). Activating a stage only writes
/// entries into verified-protocols.json; it sends nothing. Real writes additionally need session arming (typed phrase).
/// Stages are cumulative by order (rgb → png → gif) and deactivating a stage also deactivates the later ones.
/// </summary>
public static class ActivationStages
{
    public const ushort Vid = 0x0C45;
    public const ushort Pid = 0x8009;
    public const ushort ControlUsagePage = 0xFF13;
    public const ushort DataUsagePage = 0xFF68;
    public const int ControlFeatureLength = 65;
    public const int DataOutputLength = 4097;

    public static readonly IReadOnlyList<StageInfo> Infos = new[]
    {
        new StageInfo(Stages.Rgb, "Этап 1 · RGB",
            "Цвет, яркость, скорость, режим подсветки.",
            "Feature-отчёты 65 Б на FF13: START, MODE_CFG/MODE_DATA, SAVE (и FINISH, если включён). Экран и картинки НЕ затрагиваются.",
            "Подтверждён эталоном (kabelklaus, USB): подсветка меняется, клавиатура работает.",
            "Клавиатура подключена кабелем в USB-режиме, фирменное ПО AJAZZ закрыто."),
        new StageInfo(Stages.Png, "Этап 2 · PNG (один кадр)",
            "Загрузка одного статичного кадра на TFT.",
            "Те же feature-команды + IMAGE_CFG и 4096-байтные output-отчёты на FF68 (вывод 4097 Б). Только оформления из ОДНОГО кадра.",
            "Подтверждён эталоном: загрузка PNG работает.",
            "Этап 1 активирован и успешно проверен в KORTY."),
        new StageInfo(Stages.Gif, "Этап 3 · GIF (анимация)",
            "Загрузка анимации (несколько кадров).",
            "То же, что этап 2, но для оформлений из нескольких кадров. Ёмкость экрана не проверена — начинать с 5–10 кадров.",
            "Эталон умеет GIF с задержками, KORTY — через тот же протокол; на вашем железе GIF ещё не проверялся.",
            "Этап 2 активирован и успешно проверен в KORTY."),
        new StageInfo(Stages.Settings, "Этап 4 · Настройки клавиатуры",
            "Время отклика клавиш (5 уровней), время сна, отключение клавиши Windows / Alt+F4 / Alt+Tab.",
            "Feature-команды 65 Б на FF13: START, SETTINGS_CFG/SETTINGS_DATA, SAVE. Экран и подсветка не затрагиваются. Отправляется один блок со всеми значениями со страницы «Клавиатура».",
            "Формат блока восстановлен из кода и базы фирменной программы; на вашей клавиатуре ещё не проверялся.",
            "Этап 1 включён."),
    };

    public static StageInfo Info(string id) => Infos.First(i => i.Id == id);

    public static IReadOnlyList<VerifiedEntry> EntriesFor(string stage, string verifiedOn)
    {
        var by = "Пользователь (согласие в KORTY)";
        var ev = $"Протокол подтверждён эталонным веб-конфигуратором на AK820 Pro (USB 0C45:8009; FF13 feature 65; FF68 output 4097). Этап «{stage}» включён пользователем.";
        VerifiedEntry E(int cmd, ushort page, ReportKind kind, int len) => new()
        {
            Family = ProtocolFamily.Feature04, CommandId = cmd, VendorId = Vid, ProductId = Pid, UsagePage = page,
            Kind = kind, ReportLength = len, Stage = stage, VerifiedBy = by, VerifiedOn = verifiedOn, Evidence = ev,
        };
        var control = (int cmd) => E(cmd, ControlUsagePage, ReportKind.Feature, ControlFeatureLength);
        return stage switch
        {
            Stages.Rgb => new[] { control(0x18), control(0x13), control(0x02), control(0xF0) },
            Stages.Settings => new[] { control(0x18), control(0x17), control(0x02), control(0xF0) },
            Stages.Png or Stages.Gif => new[] { control(0x18), control(0x72), control(0x02), control(0xF0), E(0x72, DataUsagePage, ReportKind.Output, DataOutputLength) },
            _ => throw new ArgumentException("unknown stage", nameof(stage)),
        };
    }

    public static IReadOnlyList<string> ActiveStages(VerifiedProtocolRegistry registry) =>
        Stages.All.Where(s => registry.Entries.Any(e => e.Stage == s)).ToList();

    /// <summary>Stage that must be active first (rgb is the base for everything).</summary>
    public static string? PrerequisiteOf(string stage) => stage switch { Stages.Png => Stages.Rgb, Stages.Gif => Stages.Png, Stages.Settings => Stages.Rgb, _ => null };

    /// <summary>Why <paramref name="stage"/> cannot be activated right now (null = it can).</summary>
    public static string? CannotActivate(string stage, VerifiedProtocolRegistry registry)
    {
        var active = ActiveStages(registry);
        if (active.Contains(stage)) return "уже активирован";
        var pre = PrerequisiteOf(stage);
        if (pre != null && !active.Contains(pre)) return $"сначала активируйте и проверьте «{Info(pre).Title}»";
        return null;
    }

    private static List<VerifiedEntry> Read(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<VerifiedEntry>>(File.ReadAllText(path), VerifiedProtocolRegistry.JsonOptions) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Writes the stage's entries into the registry file (atomically). Returns the reloaded registry.</summary>
    public static VerifiedProtocolRegistry Activate(string path, string stage, DateTime? now = null)
    {
        var current = VerifiedProtocolRegistry.Load(path);
        var why = CannotActivate(stage, current);
        if (why != null) throw new InvalidOperationException($"Этап «{Info(stage).Title}»: {why}.");
        var all = Read(path).Where(e => e.Stage != stage).ToList();
        all.AddRange(EntriesFor(stage, (now ?? DateTime.Now).ToString("yyyy-MM-dd")));
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(all, VerifiedProtocolRegistry.JsonOptions));
        return VerifiedProtocolRegistry.Load(path);
    }

    /// <summary>Activates every stage in dependency order (the one-click consent flow). Returns the reloaded registry.</summary>
    public static VerifiedProtocolRegistry ActivateAll(string path, DateTime? now = null)
    {
        var reg = VerifiedProtocolRegistry.Load(path);
        foreach (var st in Stages.All)
            if (CannotActivate(st, reg) == null) reg = Activate(path, st, now);
        return reg;
    }

    /// <summary>Removes the stage and every stage that depends on it.</summary>
    public static VerifiedProtocolRegistry Deactivate(string path, string stage)
    {
        var drop = new HashSet<string> { stage };
        bool grew = true;
        while (grew) { grew = false; foreach (var st in Stages.All) if (!drop.Contains(st) && PrerequisiteOf(st) is { } p && drop.Contains(p)) { drop.Add(st); grew = true; } }
        var keep = Read(path).Where(e => !drop.Contains(e.Stage)).ToList();
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(keep, VerifiedProtocolRegistry.JsonOptions));
        return VerifiedProtocolRegistry.Load(path);
    }
}
