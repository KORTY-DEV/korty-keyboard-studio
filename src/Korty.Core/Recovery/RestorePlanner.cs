using Korty.Core.Discovery;
using Korty.Core.Protocol;
using Korty.Core.Storage;

namespace Korty.Core.Recovery;

public enum StepStatus { Ready, Blocked, Info }

public sealed record RestoreStep(string Title, string Detail, StepStatus Status, IReadOnlyList<ProtocolPacket>? Packets = null);

public sealed record RestorePlan(string ConnectionKey, IReadOnlyList<RestoreStep> Steps)
{
    /// <summary>True only if every packet-carrying step passes the full write-gate preflight.</summary>
    public bool CanExecute => Steps.Any(s => s.Packets is { Count: > 0 }) && Steps.All(s => s.Status != StepStatus.Blocked);
}

/// <summary>
/// Part C ("state restoration") as an explicit, inspectable plan. Whether the firmware forgets the screen after a
/// USB↔2.4 GHz switch is UNKNOWN (hypothesis, see docs); this planner only answers "if it did, what would we send and may we?".
/// Execution never happens without the gate allowing every packet.
/// </summary>
public sealed class RestorePlanner
{
    private readonly DeviceCommandService _service;
    public RestorePlanner(DeviceCommandService service) => _service = service;

    public RestorePlan Plan(string connectionKey, ConnectionProfile profile, PhysicalDevice? device,
        IReadOnlyList<ProtocolPacket> rgbPackets, IReadOnlyList<ProtocolPacket>? tftPackets, string? designName)
    {
        var steps = new List<RestoreStep>();
        if (device == null)
        {
            steps.Add(new("Ожидание устройства", "Подходящее устройство для этого режима подключения не обнаружено.", StepStatus.Blocked));
            return new RestorePlan(connectionKey, steps);
        }
        steps.Add(new("Устройство", $"{device.Title} · {device.VidPid} · vendor-интерфейсов: {device.VendorInterfaceCount}", StepStatus.Info));
        steps.Add(new("Стабилизация", "Подождать, пока набор интерфейсов не перестанет меняться (≥2 подряд одинаковых опроса), прежде чем что-либо отправлять.", StepStatus.Info));

        var rgb = _service.Check(rgbPackets, device);
        steps.Add(new("Подсветка", $"{rgbPackets.Count} пакет(ов): {(rgbPackets.Count > 0 ? rgbPackets[0].Description : "—")}",
            rgb.AllAllowed ? StepStatus.Ready : StepStatus.Blocked, rgbPackets));
        if (!rgb.AllAllowed) steps.Add(new("  ↳ причина", string.Join("; ", rgb.DistinctBlockers), StepStatus.Info));

        if (tftPackets == null || profile.DesignId == null)
            steps.Add(new("Экран TFT", "Для этого подключения оформление не назначено.", StepStatus.Info));
        else
        {
            var tft = _service.Check(tftPackets, device);
            steps.Add(new("Экран TFT", $"«{designName}» · {tftPackets.Count} пакет(ов) ({tftPackets.Sum(p => (long)p.WireLength) / 1024} КиБ)",
                tft.AllAllowed ? StepStatus.Ready : StepStatus.Blocked, tftPackets));
            if (!tft.AllAllowed) steps.Add(new("  ↳ причина", string.Join("; ", tft.DistinctBlockers), StepStatus.Info));
        }
        steps.Add(new("Проверка", "Прочитать состояние обратно невозможно: подтверждённой команды чтения, пригодной для проверки экрана, нет — результат оценивает владелец визуально.", StepStatus.Info));
        return new RestorePlan(connectionKey, steps);
    }

    public async Task<CommandOutcome> ExecuteAsync(RestorePlan plan, PhysicalDevice? device, IProgress<double>? progress, CancellationToken ct)
    {
        if (!plan.CanExecute) return new CommandOutcome(false, 0, 0, "План заблокирован — выполнять нечего.");
        int sent = 0, total = plan.Steps.Where(s => s.Packets != null).Sum(s => s.Packets!.Count);
        foreach (var step in plan.Steps.Where(s => s.Packets is { Count: > 0 }))
        {
            var r = await _service.SendAsync(step.Packets!, device, null, ct).ConfigureAwait(false);
            if (!r.Success) return new CommandOutcome(false, sent + r.Sent, total, $"{step.Title}: {r.Message}");
            sent += r.Sent;
            progress?.Report((double)sent / total);
        }
        return new CommandOutcome(true, sent, total, $"Восстановление выполнено: {sent} пакетов");
    }
}
