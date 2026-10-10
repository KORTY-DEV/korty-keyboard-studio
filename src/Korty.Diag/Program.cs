using Korty.Core.Diagnostics;
using Korty.Core.Discovery;
using Korty.Hid.Windows;

// korty-diag: READ-ONLY HID descriptor discovery. Sends nothing to any device.
//   korty-diag scan                      human-readable topology of VID 0C45 devices
//   korty-diag snapshot <label> <file>   save a labelled snapshot (for differential diagnostics)
//   korty-diag diff <fileA> <fileB>      compare two snapshots
//   korty-diag report <file>             write the shareable diagnostic JSON (no serials, no paths)
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            switch (args.FirstOrDefault())
            {
                case "scan": return Scan();
                case "snapshot" when args.Length == 3: return Snapshot(args[1], args[2]);
                case "diff" when args.Length == 3: return Diff(args[1], args[2]);
                case "report" when args.Length == 2: return Report(args[1]);
                default:
                    Console.WriteLine("Usage: korty-diag scan | snapshot <label> <file> | diff <a> <b> | report <file>   (read-only)");
                    return 2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
    }

    private static int Scan()
    {
        var all = new WindowsHidEnumerator().Enumerate();
        var topo = DeviceClassifier.Classify(all);
        Console.WriteLine($"HID interfaces total: {topo.TotalHidInterfaces}; other-vendor (not opened): {topo.IgnoredOtherVendorInterfaces}");
        if (topo.Devices.Count == 0) Console.WriteLine("No VID 0C45 devices found.");
        foreach (var d in topo.Devices)
        {
            Console.WriteLine($"\n== {d.Title}  [{d.VidPid}]  kind={d.Kind}  actionable={d.IsActionable}");
            foreach (var n in d.Notes) Console.WriteLine("   note: " + n);
            foreach (var i in d.Interfaces)
                Console.WriteLine($"   MI{i.Info.InterfaceNumber:00} COL{i.Info.CollectionNumber:00}  usage={i.Info.UsageLabel}  in/out/feat={i.Info.InputReportLength}/{i.Info.OutputReportLength}/{i.Info.FeatureReportLength}  {i.Role}  name='{i.Info.ProductName}'  desc={i.Info.DescriptorRead}");
        }
        return 0;
    }

    private static int Snapshot(string label, string file)
    {
        var snap = DeviceSnapshot.Capture(label, new WindowsHidEnumerator().Enumerate());
        File.WriteAllText(file, DifferentialAnalyzer.Serialize(snap));
        Console.WriteLine($"Saved snapshot '{label}': {snap.Interfaces.Count} interfaces, PIDs: {string.Join(", ", snap.Pids)}");
        return 0;
    }

    private static int Diff(string a, string b)
    {
        var sa = DifferentialAnalyzer.Deserialize(File.ReadAllText(a)) ?? throw new InvalidDataException(a);
        var sb = DifferentialAnalyzer.Deserialize(File.ReadAllText(b)) ?? throw new InvalidDataException(b);
        var d = DifferentialAnalyzer.Compare(sa, sb);
        Console.WriteLine($"Only in '{d.LabelA}': {d.OnlyInA.Count}; only in '{d.LabelB}': {d.OnlyInB.Count}; common: {d.Common.Count}");
        foreach (var c in d.Conclusions) Console.WriteLine(" - " + c);
        return 0;
    }

    private static int Report(string file)
    {
        var topo = DeviceClassifier.Classify(new WindowsHidEnumerator().Enumerate());
        File.WriteAllText(file, DiagnosticReport.Build(topo, Array.Empty<DeviceSnapshot>(), false, 0, new DiagLog()));
        Console.WriteLine("Saved " + file);
        return 0;
    }
}
