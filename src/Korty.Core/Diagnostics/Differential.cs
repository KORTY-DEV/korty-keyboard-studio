using System.Text.Json;
using Korty.Core.Discovery;
using Korty.Core.Hid;
using Korty.Core.Storage;

namespace Korty.Core.Diagnostics;

public sealed record InterfaceFingerprint(string Fingerprint, string VidPid, string Usage, int? InterfaceNumber, int? Collection, int In, int Out, int Feature, string? ProductName);

/// <summary>A labelled set of descriptor facts captured while the owner arranged a known physical situation (e.g. "only cable").</summary>
public sealed record DeviceSnapshot(string Label, DateTimeOffset CapturedAt, IReadOnlyList<InterfaceFingerprint> Interfaces)
{
    public static DeviceSnapshot Capture(string label, IEnumerable<HidInterfaceInfo> all, bool onlyVendor0C45 = true)
    {
        var items = all.Where(i => !onlyVendor0C45 || i.VendorId == DeviceClassifier.SonixVid)
            .Select(i => new InterfaceFingerprint(i.Fingerprint, i.VidPid, i.UsageLabel, i.InterfaceNumber, i.CollectionNumber,
                i.InputReportLength, i.OutputReportLength, i.FeatureReportLength, i.ProductName))
            .OrderBy(i => i.Fingerprint, StringComparer.Ordinal).ToList();
        return new DeviceSnapshot(label, DateTimeOffset.Now, items);
    }

    public IEnumerable<string> Pids => Interfaces.Select(i => i.VidPid).Distinct();
}

public sealed record SnapshotDiff(
    string LabelA, string LabelB,
    IReadOnlyList<InterfaceFingerprint> OnlyInA,
    IReadOnlyList<InterfaceFingerprint> OnlyInB,
    IReadOnlyList<InterfaceFingerprint> Common,
    IReadOnlyList<string> Conclusions);

/// <summary>
/// Dedicated safe procedure for settling "which VID/PID is which connection". Pure comparison of descriptor facts.
/// Recommended sequence (see docs): A = receiver unplugged + keyboard on cable; B = cable unplugged + receiver plugged +
/// keyboard in 2.4 GHz; C = both. A PID that appears only in A belongs to the cable path, only in B to the receiver path.
/// </summary>
public static class DifferentialAnalyzer
{
    public static SnapshotDiff Compare(DeviceSnapshot a, DeviceSnapshot b)
    {
        var setA = a.Interfaces.ToDictionary(i => i.Fingerprint);
        var setB = b.Interfaces.ToDictionary(i => i.Fingerprint);
        var onlyA = a.Interfaces.Where(i => !setB.ContainsKey(i.Fingerprint)).ToList();
        var onlyB = b.Interfaces.Where(i => !setA.ContainsKey(i.Fingerprint)).ToList();
        var common = a.Interfaces.Where(i => setB.ContainsKey(i.Fingerprint)).ToList();

        var conclusions = new List<string>();
        var pidsA = a.Pids.ToHashSet(); var pidsB = b.Pids.ToHashSet();
        foreach (var pid in pidsA.Except(pidsB))
            conclusions.Add($"{pid} присутствует только в «{a.Label}» → относится к тому, что было подключено ТОЛЬКО в этой ситуации.");
        foreach (var pid in pidsB.Except(pidsA))
            conclusions.Add($"{pid} присутствует только в «{b.Label}» → относится к тому, что было подключено ТОЛЬКО в этой ситуации.");
        foreach (var pid in pidsA.Intersect(pidsB))
        {
            var na = a.Interfaces.Count(i => i.VidPid == pid); var nb = b.Interfaces.Count(i => i.VidPid == pid);
            conclusions.Add(na == nb
                ? $"{pid} есть в обоих снимках ({na} интерф.): по одному PID нельзя отличить режимы — сравнивайте условия снимков."
                : $"{pid} есть в обоих снимках, но число интерфейсов отличается ({na} vs {nb}).");
        }
        if (onlyA.Count == 0 && onlyB.Count == 0) conclusions.Add("Снимки идентичны — условия съёмки, вероятно, не различались.");
        conclusions.Add("Вывод носит рабочий характер: подтверждение соответствия PID↔режим требует, чтобы владелец гарантировал условия съёмки (что именно было физически подключено).");
        return new SnapshotDiff(a.Label, b.Label, onlyA, onlyB, common, conclusions);
    }

    public static string Serialize(DeviceSnapshot s) => JsonSerializer.Serialize(s, AtomicFile.Json);
    public static DeviceSnapshot? Deserialize(string json) => JsonSerializer.Deserialize<DeviceSnapshot>(json, AtomicFile.Json);
}
