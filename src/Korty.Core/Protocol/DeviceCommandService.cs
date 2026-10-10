using Korty.Core.Diagnostics;
using Korty.Core.Discovery;
using Korty.Core.Hid;
using Korty.Core.Safety;

namespace Korty.Core.Protocol;

public sealed record PreflightItem(ProtocolPacket Packet, HidInterfaceInfo? Target, WriteDecision? Decision, string? ResolveError)
{
    public bool Ok => Target != null && Decision is { Allowed: true };
    public string Reason => ResolveError ?? Decision?.Summary ?? "";
}

public sealed record Preflight(IReadOnlyList<PreflightItem> Items)
{
    public bool AllAllowed => Items.Count > 0 && Items.All(i => i.Ok);
    public IReadOnlyList<string> DistinctBlockers => Items.Where(i => !i.Ok).SelectMany(i => i.Reason.Split("; ")).Distinct().ToList();
}

public sealed record CommandOutcome(bool Success, int Sent, int Total, string Message)
{
    /// <summary>Output reports for which no input report (ACK) arrived within the wait. The reference tolerates this; it is reported, not hidden.</summary>
    public int AcksMissed { get; init; }
    public int AcksExpected { get; init; }
}

/// <summary>
/// Application service between UI and transport. The UI hands over typed packets; this class chooses the interface
/// WITHOUT guessing, asks <see cref="WriteGate"/> about every packet BEFORE the first byte is sent (all-or-nothing),
/// then sends sequentially and aborts on the first failure.
/// </summary>
public sealed class DeviceCommandService
{
    private readonly WriteGate _gate;
    private readonly IHidTransport _transport;
    private readonly DiagLog _log;

    public DeviceCommandService(WriteGate gate, IHidTransport transport, DiagLog log)
    {
        _gate = gate; _transport = transport; _log = log;
    }

    /// <summary>The control/data interface for a packet: vendor-defined and descriptor-length exact. Ambiguity ⇒ refuse.</summary>
    /// <summary>Usage page the family's documented/working description uses for each role (Feature04: FF13 control, FF68 data).</summary>
    public static ushort ExpectedUsagePage(ProtocolFamily family, TargetRole role) => (family, role) switch
    {
        (ProtocolFamily.Feature04, TargetRole.Control) => 0xFF13,
        (ProtocolFamily.Feature04, TargetRole.Data) => 0xFF68,
        (ProtocolFamily.AaFrame, TargetRole.Control) => 0xFF68,
        _ => 0xFF67,
    };

    public static (HidInterfaceInfo? target, string? error) Resolve(PhysicalDevice device, ProtocolPacket packet)
    {
        var page = ExpectedUsagePage(packet.Family, packet.Target);
        var candidates = device.Interfaces
            .Where(i => i.Role == InterfaceRole.VendorDefined)
            .Select(i => i.Info)
            .Where(i => i.UsagePage == page)
            .Where(i => packet.Kind == ReportKind.Output ? i.OutputReportLength == packet.WireLength : i.FeatureReportLength == packet.WireLength)
            .ToList();
        if (candidates.Count == 1) return (candidates[0], null);
        if (candidates.Count == 0)
            return (null, $"нет vendor-интерфейса {page:X4} с {(packet.Kind == ReportKind.Output ? "output" : "feature")}-отчётом длиной {packet.WireLength} Б (нужен для {packet.CommandName})");
        return (null, $"неоднозначно: {candidates.Count} интерфейсов подходят под {packet.CommandName} — без аппаратной проверки выбор не делается");
    }

    /// <summary>Plain-language advice for the most common transfer failures (the raw Win32 code stays in the message).</summary>
    public static string Hint(string? error, ProtocolPacket p)
    {
        var e = error ?? "";
        string where = p.Kind == ReportKind.Output ? "передача данных экрана" : "команда управления";
        if (e.Contains("CreateFile")) return " · Совет: закройте фирменное ПО AJAZZ и вкладки веб-конфигуратора, затем повторите ОДИН раз.";
        if (e.Contains("Win32 31") || e.Contains("Win32 1117") || e.Contains("Win32 121")) return $" · Совет: клавиатура не приняла ({where}). Выдерните и вставьте кабель, подождите 5 с, не повторяйте подряд.";
        if (e.Contains("Win32 995") || e.Contains("не принял")) return " · Совет: клавиатура долго не отвечала и операция отменена. Переподключите кабель; для GIF начните с меньшего числа кадров.";
        if (e.Contains("Win32 1167") || e.Contains("Win32 433")) return " · Совет: устройство отключилось во время передачи — проверьте кабель/порт.";
        return " · Совет: переподключите кабель и повторите один раз; журнал содержит точный код ошибки.";
    }

    private static string Hex(byte[] b, int max) => string.Join(" ", b.Take(max).Select(x => x.ToString("X2"))) + (b.Length > max ? " …" : "");

    public Preflight Check(IReadOnlyList<ProtocolPacket> packets, PhysicalDevice? device)
    {
        var items = new List<PreflightItem>(packets.Count);
        // Decisions are cached per (command, target, length) — a 2000-chunk upload should not produce 2000 log lines.
        var cache = new Dictionary<(ProtocolFamily, int, string, int, string?), WriteDecision>();
        foreach (var p in packets)
        {
            if (device == null) { items.Add(new(p, null, null, "устройство не подключено")); continue; }
            var (target, err) = Resolve(device, p);
            if (target == null) { items.Add(new(p, null, null, err)); continue; }
            var key = (p.Family, p.CommandId, target.Path, p.WireLength, p.Stage);
            if (!cache.TryGetValue(key, out var dec)) cache[key] = dec = _gate.Evaluate(p, target);
            // A cached decision carries the Approved token of the FIRST packet; re-evaluate cheaply for the token.
            items.Add(new(p, target, dec.Allowed ? _gate.Evaluate(p, target) : dec, null));
        }
        return new Preflight(items);
    }

    public async Task<CommandOutcome> SendAsync(IReadOnlyList<ProtocolPacket> packets, PhysicalDevice? device, IProgress<double>? progress, CancellationToken ct)
    {
        var pre = Check(packets, device);
        if (!pre.AllAllowed)
        {
            var msg = "Отправка заблокирована: " + string.Join("; ", pre.DistinctBlockers);
            _log.Warn("send", msg);
            return new CommandOutcome(false, 0, packets.Count, msg);
        }
        int sent = 0, acksExpected = 0, acksMissed = 0;
        foreach (var item in pre.Items)
        {
            ct.ThrowIfCancellationRequested();
            var p = item.Packet;
            TransportResult r;
            try { r = await _transport.SendAsync(item.Decision!.Approved!, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r = TransportResult.Fail(ex.Message); }
            if (!r.Success)
            {
                var msg = $"Сбой на пакете {sent + 1}/{packets.Count} ({p.CommandName}): {r.Error}{Hint(r.Error, p)}";
                _log.Error("send", msg);
                return new CommandOutcome(false, sent, packets.Count, msg) { AcksExpected = acksExpected, AcksMissed = acksMissed };
            }
            if (p.AckWaitMs > 0) { acksExpected++; if (r.Response == null) acksMissed++; }
            if (p.HandshakeAfterMs != null)
                _log.Info("send", $"{p.CommandName}: ответ устройства {(r.Response == null ? "нет (не критично)" : Hex(r.Response, 16))}");
            sent++;
            if (p.SettleMs > 0) await Task.Delay(p.SettleMs, ct).ConfigureAwait(false);
            progress?.Report((double)sent / packets.Count);
        }
        string ackText = acksExpected == 0 ? "" : acksMissed == 0 ? $" · ACK получены: {acksExpected}/{acksExpected}" : $" · ACK без ответа: {acksMissed} из {acksExpected}";
        _log.Info("send", $"Отправлено {sent}/{packets.Count}: {packets[0].CommandName}…{ackText}");
        return new CommandOutcome(true, sent, packets.Count, $"Отправлено пакетов: {sent}{ackText}") { AcksExpected = acksExpected, AcksMissed = acksMissed };
    }
}
