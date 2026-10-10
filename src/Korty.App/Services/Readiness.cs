using Korty.Core.Protocol;
using Korty.Core.Safety;
using Korty.App.ViewModels;

namespace Korty.App;

/// <summary>Plain-language pre-flight list for a real write: device → interfaces → stage → session permission. Sends nothing.</summary>
public static class Readiness
{
    public static IReadOnlyList<StepRow> Evaluate(AppHost host, string stage, IReadOnlyList<ProtocolPacket> packets)
    {
        var rows = new List<StepRow>();
        void Add(bool ok, string title, string detail) => rows.Add(new StepRow(title, detail, ok ? "✓" : "✕", ok ? Theme.Good : Theme.Bad));

        var dev = host.TargetDevice;
        Add(dev is { IsActionable: true }, "Клавиатура",
            dev == null ? "для выбранного подключения (USB-C / 2.4 GHz) устройство не обнаружено" : $"{dev.Title} · {dev.VidPid}");

        bool ifOk = dev != null;
        var details = new List<string>();
        if (dev != null)
        {
            foreach (var p in packets.GroupBy(x => (x.Target, x.Kind, x.WireLength)).Select(g => g.First()))
            {
                var (t, err) = DeviceCommandService.Resolve(dev, p);
                if (t == null) { ifOk = false; details.Add(err ?? "интерфейс не найден"); }
                else details.Add($"{t.UsageLabel} · {(p.Kind == Korty.Core.Hid.ReportKind.Feature ? "feature" : "output")} {p.WireLength} Б");
            }
        }
        Add(ifOk, "Интерфейсы", dev == null ? "—" : string.Join(" · ", details.Distinct()));

        bool sim = host.SimulationMode;
        Add(sim || host.ControlEnabled, "Управление клавиатурой",
            sim ? "симулятор" : host.ControlEnabled ? "включено" : "выключено — нажмите «Включить управление клавиатурой»");
        return rows;
    }
}
