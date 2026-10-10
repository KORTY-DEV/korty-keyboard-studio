using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Korty.Core.Discovery;
using Korty.Core.Storage;

namespace Korty.Core.Diagnostics;

/// <summary>Shareable JSON for the next engineering step. Contains no serial numbers, no device paths, no user name.</summary>
public static class DiagnosticReport
{
    public static string Build(DeviceTopology topology, IEnumerable<DeviceSnapshot> snapshots, bool writeGateArmed, int verifiedEntries, DiagLog log, bool allHidDevicesIncluded = false)
    {
        var report = new
        {
            Tool = "KORTY Keyboard Studio",
            Version = typeof(DiagnosticReport).Assembly.GetName().Version?.ToString(),
            CapturedAt = DateTimeOffset.Now,
            Os = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            Safety = new { WriteGateArmed = writeGateArmed, HardwareVerifiedEntries = verifiedEntries, HidWritesPerformedByDiscovery = 0 },
            Topology = new
            {
                topology.TotalHidInterfaces,
                IgnoredOtherVendorInterfaces = topology.IgnoredOtherVendorInterfaces,
                Devices = topology.Devices.Select(d => new
                {
                    d.VidPid,
                    Kind = d.Kind.ToString(),
                    d.Title,
                    d.Notes,
                    Interfaces = d.Interfaces.Select(i => new
                    {
                        Role = i.Role.ToString(),
                        Mi = i.Info.InterfaceNumber,
                        Col = i.Info.CollectionNumber,
                        UsagePage = $"{i.Info.UsagePage:X4}",
                        Usage = $"{i.Info.Usage:X4}",
                        Input = i.Info.InputReportLength,
                        Output = i.Info.OutputReportLength,
                        Feature = i.Info.FeatureReportLength,
                        i.Info.ProductName,
                        i.Info.DescriptorRead,
                        i.Hint,
                    }),
                }),
            },
            Snapshots = snapshots,
            RecentLog = log.Snapshot().TakeLast(200).Select(e => e.ToString()),
        };
        return JsonSerializer.Serialize(report, AtomicFile.Json);
    }
}
