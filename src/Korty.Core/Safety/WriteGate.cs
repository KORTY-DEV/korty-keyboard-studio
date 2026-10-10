using System.Text.Json;
using System.Text.Json.Serialization;
using Korty.Core.Hid;
using Korty.Core.Protocol;

namespace Korty.Core.Safety;

/// <summary>
/// Proof that <see cref="WriteGate"/> approved exactly this packet for exactly this interface.
/// The constructor is internal: the only producer in the whole code base is <see cref="WriteGate.Evaluate"/>.
/// </summary>
public sealed class ApprovedWrite
{
    public HidInterfaceInfo Target { get; }
    public ProtocolPacket Packet { get; }
    internal ApprovedWrite(HidInterfaceInfo target, ProtocolPacket packet) { Target = target; Packet = packet; }
}

public sealed record WriteDecision(bool Allowed, IReadOnlyList<string> Reasons, ApprovedWrite? Approved)
{
    public string Summary => Allowed ? "разрешено" : string.Join("; ", Reasons);
}

/// <summary>One owner-verified (command × interface) combination. See docs/HARDWARE_ACCEPTANCE.md for how entries are earned.</summary>
public sealed class VerifiedEntry
{
    public ProtocolFamily Family { get; set; }
    public int CommandId { get; set; }
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public ushort UsagePage { get; set; }
    public ReportKind Kind { get; set; }
    /// <summary>Activation stage ("rgb", "png", "gif"); a packet is only allowed by entries of its own stage.</summary>
    public string Stage { get; set; } = "";
    /// <summary>Exact wire length (report id + body) the descriptor must advertise.</summary>
    public int ReportLength { get; set; }
    public string VerifiedBy { get; set; } = "";
    public string VerifiedOn { get; set; } = "";
    public string Evidence { get; set; } = "";
}

public sealed class VerifiedProtocolRegistry
{
    private readonly List<VerifiedEntry> _entries = new();
    public IReadOnlyList<VerifiedEntry> Entries => _entries;
    public IReadOnlyList<string> LoadProblems { get; private set; } = Array.Empty<string>();

    /// <summary>v0.2 ships with NOTHING hardware-verified.</summary>
    public static VerifiedProtocolRegistry Empty() => new();

    /// <summary>
    /// Loads owner-authored entries. Entries for destructive/unknown commands are discarded, as are incomplete ones.
    /// A missing or corrupt file yields an empty registry (fail closed).
    /// </summary>
    public static VerifiedProtocolRegistry Load(string path)
    {
        var reg = new VerifiedProtocolRegistry();
        var problems = new List<string>();
        try
        {
            if (!File.Exists(path)) return reg;
            var list = JsonSerializer.Deserialize<List<VerifiedEntry>>(File.ReadAllText(path), JsonOptions) ?? new();
            foreach (var e in list)
            {
                var cmd = CommandCatalog.Find(e.Family, e.CommandId);
                if (cmd is null || cmd.NeverSendable) { problems.Add($"Отклонена запись {e.Family}/0x{e.CommandId:X2}: команда {(cmd is null ? "неизвестна" : "заблокирована навсегда")}."); continue; }
                if (e.VendorId != 0x0C45 || e.UsagePage < 0xFF00 || e.ReportLength < 2) { problems.Add($"Отклонена запись 0x{e.CommandId:X2}: некорректный VID/UsagePage/длина."); continue; }
                if (!Stages.All.Contains(e.Stage)) { problems.Add($"Отклонена запись 0x{e.CommandId:X2}: неизвестный этап «{e.Stage}»."); continue; }
                if (string.IsNullOrWhiteSpace(e.VerifiedBy) || string.IsNullOrWhiteSpace(e.Evidence)) { problems.Add($"Отклонена запись 0x{e.CommandId:X2}: нет VerifiedBy/Evidence."); continue; }
                reg._entries.Add(e);
            }
        }
        catch (Exception ex) { problems.Add("verified-protocols.json не прочитан (реестр пуст): " + ex.Message); reg._entries.Clear(); }
        reg.LoadProblems = problems;
        return reg;
    }

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public bool Contains(ProtocolPacket p, HidInterfaceInfo t) => _entries.Any(e =>
        e.Family == p.Family && e.CommandId == p.CommandId && e.VendorId == t.VendorId && e.ProductId == t.ProductId &&
        e.UsagePage == t.UsagePage && e.Kind == p.Kind && e.ReportLength == p.WireLength &&
        string.Equals(e.Stage, p.Stage, StringComparison.Ordinal));
}

/// <summary>
/// Single choke point for every device write. Defaults are fail-closed:
/// <list type="bullet">
/// <item>Real hardware: writes need (1) session arming with an exact typed phrase, (2) a hardware-verified registry entry
///   for this command AND this exact interface descriptor, (3) a non-destructive command, (4) exact report length match,
///   (5) a vendor-defined, non-bootloader interface of VID 0C45.</item>
/// <item>Simulated interfaces (path "sim://…") never reach real hardware and need no arming.</item>
/// </list>
/// The app re-enables writes at start only if the user has already given consent (registry entries exist).
/// </summary>
public sealed class WriteGate
{
    public const ushort BootloaderPid = 0x7140;

    private VerifiedProtocolRegistry _registry;
    private readonly Action<string>? _audit;
    public bool Armed { get; private set; }
    public VerifiedProtocolRegistry VerifiedRegistry => _registry;

    /// <summary>Called after the owner activated/deactivated a stage (file rewritten). Does not change arming.</summary>
    public void ReplaceRegistry(VerifiedProtocolRegistry registry)
    {
        _registry = registry;
        _audit?.Invoke($"GATE: registry reloaded ({registry.Entries.Count} entries, stages: {string.Join(",", registry.Entries.Select(e => e.Stage).Distinct())})");
    }

    public WriteGate(VerifiedProtocolRegistry registry, Action<string>? audit = null)
    {
        _registry = registry;
        _audit = audit;
    }

    /// <summary>User switched control of the keyboard on (one consent in the UI). Real writes still need activated stages and exact descriptor matches.</summary>
    public void Arm()
    {
        Armed = true;
        _audit?.Invoke("GATE: writes ENABLED by the user");
    }

    public void Disarm() { Armed = false; _audit?.Invoke("GATE: disarmed"); }

    public static string StageTitle(string stage) => stage switch { Stages.Rgb => "RGB", Stages.Png => "PNG", Stages.Gif => "GIF", Stages.Settings => "Настройки", _ => stage };

    public WriteDecision Evaluate(ProtocolPacket packet, HidInterfaceInfo target)
    {
        var why = new List<string>();
        var cmd = CommandCatalog.Find(packet.Family, packet.CommandId);
        // IMAGE_DATA reuses the announcing command id inside the 04 family; the catalog entry for 0x72 covers it.
        if (cmd is null) why.Add($"команда 0x{packet.CommandId:X2} не входит в каталог");
        else if (cmd.NeverSendable) why.Add($"команда {cmd.Name} заблокирована навсегда ({cmd.Risk})");
        if (packet.Risk is CommandRisk.Destructive or CommandRisk.Unknown) why.Add($"класс риска пакета: {packet.Risk}");
        if (packet.Body.Length == 0) why.Add("пустой пакет");
        if (packet.Kind == ReportKind.Output && target.OutputReportLength != packet.WireLength) why.Add($"длина output-отчёта {packet.WireLength} ≠ {target.OutputReportLength} по дескриптору");
        if (packet.Kind == ReportKind.Feature && target.FeatureReportLength != packet.WireLength) why.Add($"длина feature-отчёта {packet.WireLength} ≠ {target.FeatureReportLength} по дескриптору");
        if (!target.DescriptorRead) why.Add("дескриптор интерфейса не прочитан");

        if (target.IsSimulated)
        {
            // Simulation: no hardware involved.
        }
        else
        {
            if (!Armed) why.Add("запись в клавиатуру выключена (Безопасность → «Включить управление клавиатурой»)");
            if (target.VendorId != 0x0C45) why.Add($"VID {target.VendorId:X4} не 0C45 — чужое устройство");
            if (target.ProductId == BootloaderPid) why.Add("устройство в режиме загрузчика (0C45:7140)");
            if (!target.IsVendorDefined) why.Add($"интерфейс {target.UsageLabel} не vendor-defined (системный ввод)");
            if (string.IsNullOrEmpty(packet.Stage)) why.Add("пакет не относится ни к одному этапу активации");
            else if (!_registry.Contains(packet, target)) why.Add($"этап «{StageTitle(packet.Stage)}» не включён для этой команды и интерфейса (Безопасность → «Включить управление клавиатурой»)");
        }

        if (why.Count > 0)
        {
            _audit?.Invoke($"GATE: DENY {packet.CommandName} → {target.VidPid} {target.UsageLabel}: {string.Join("; ", why)}");
            return new WriteDecision(false, why, null);
        }
        return new WriteDecision(true, Array.Empty<string>(), new ApprovedWrite(target, packet));
    }
}
