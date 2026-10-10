using System.Text.Json;
using Korty.Core.Diagnostics;
using Korty.Core.Discovery;
using Korty.Core.Hid;
using Korty.Core.Model;
using Korty.Core.Protocol;
using Korty.Core.Safety;
using Korty.Core.Simulation;
using Korty.Core.Storage;
using Korty.Core.Tft;
using Xunit;

namespace Korty.Tests;

public class WriteGateTests
{
    private static ProtocolPacket Led() => AaFrameBuilder.SetLedEffect(new RgbSettings()) with { Stage = Stages.Rgb };
    private static VerifiedProtocolRegistry RegistryWith(VerifiedEntry e)
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "v.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { e }, VerifiedProtocolRegistry.JsonOptions));
        return VerifiedProtocolRegistry.Load(path);
    }
    private static VerifiedEntry LedEntry(int len = 65) => new()
    {
        Family = ProtocolFamily.AaFrame, CommandId = 0x23, VendorId = 0x0C45, ProductId = 0x8009, UsagePage = 0xFF68,
        Kind = ReportKind.Output, ReportLength = len, Stage = Stages.Rgb, VerifiedBy = "owner", VerifiedOn = "2026-01-01", Evidence = "test",
    };

    [Fact]
    public void RealHardwareIsDeniedByDefault()
    {
        var gate = new WriteGate(VerifiedProtocolRegistry.Empty());
        var d = gate.Evaluate(Led(), Hw.Iface());
        Assert.False(d.Allowed); Assert.Null(d.Approved);
        Assert.Contains(d.Reasons, r => r.Contains("запись в клавиатуру выключена"));
        Assert.Contains(d.Reasons, r => r.Contains("не включён"));
    }

    [Fact]
    public void ArmingAloneIsNotEnough()
    {
        var gate = new WriteGate(VerifiedProtocolRegistry.Empty());
        Assert.False(gate.Armed);
        gate.Arm();
        Assert.True(gate.Armed);
        var d = gate.Evaluate(Led(), Hw.Iface());
        Assert.False(d.Allowed);
        Assert.Contains(d.Reasons, r => r.Contains("не включён"));
    }

    [Fact]
    public void RegistryAloneIsNotEnough()
    {
        var gate = new WriteGate(RegistryWith(LedEntry()));
        var d = gate.Evaluate(Led(), Hw.Iface());
        Assert.False(d.Allowed);
        Assert.Contains(d.Reasons, r => r.Contains("запись в клавиатуру выключена"));
    }

    [Fact]
    public void ArmedPlusVerifiedEntryPlusExactDescriptorIsAllowed()
    {
        var gate = new WriteGate(RegistryWith(LedEntry()));
        gate.Arm();
        var d = gate.Evaluate(Led(), Hw.Iface());
        Assert.True(d.Allowed, d.Summary);
        Assert.NotNull(d.Approved);
    }

    [Theory]
    [InlineData(0x1234, 0x8009, 0xFF68, 65, "чужое устройство")]
    [InlineData(0x0C45, 0x7140, 0xFF68, 65, "загрузчика")]
    [InlineData(0x0C45, 0x8009, 0x0001, 65, "не vendor-defined")]
    [InlineData(0x0C45, 0x8009, 0xFF68, 64, "длина")]
    [InlineData(0x0C45, 0x8009, 0xFF68, 4105, "длина")]
    public void ArmedAndVerifiedStillRefusesWrongTargets(int vid, int pid, int page, int outLen, string expectedReason)
    {
        var entry = LedEntry();
        var gate = new WriteGate(RegistryWith(entry));
        gate.Arm();
        var iface = Hw.Iface((ushort)vid, (ushort)pid, (ushort)page, outLen);
        var d = gate.Evaluate(Led(), iface);
        Assert.False(d.Allowed);
        Assert.Contains(d.Reasons, r => r.Contains(expectedReason));
    }

    [Fact]
    public void UnreadableDescriptorIsRefused()
    {
        var gate = new WriteGate(RegistryWith(LedEntry())); gate.Arm();
        Assert.False(gate.Evaluate(Led(), Hw.Iface(read: false)).Allowed);
    }

    [Fact]
    public void DestructiveCommandsCannotBeRegisteredOrSent()
    {
        // Registry load discards the entry …
        var bad = LedEntry(); bad.CommandId = 0x0F;
        var reg = RegistryWith(bad);
        Assert.Empty(reg.Entries);
        Assert.Contains(reg.LoadProblems, p => p.Contains("заблокирована навсегда"));
        // … and even a hand-built packet with an entry injected by other means is refused by the gate.
        var factoryReset = new ProtocolPacket(ProtocolFamily.AaFrame, 0x0F, "SET_FACTORY_RESET", ReportKind.Output, TargetRole.Control, 0,
            AaFrameBuilder.Frame(0x0F, 1, 0, 0xFF), CommandRisk.Destructive, "reset");
        var gate = new WriteGate(VerifiedProtocolRegistry.Empty());
        gate.Arm();
        Assert.False(gate.Evaluate(factoryReset, Hw.Iface()).Allowed);
        // simulated interface too: destructive is refused everywhere.
        var sim = Hw.Iface(path: "sim://x");
        Assert.False(gate.Evaluate(factoryReset, sim).Allowed);
    }

    [Fact]
    public void UnknownCommandIdIsRefused()
    {
        var p = new ProtocolPacket(ProtocolFamily.AaFrame, 0x99, "MYSTERY", ReportKind.Output, TargetRole.Control, 0, AaFrameBuilder.Frame(0x99, 0), CommandRisk.VolatileSetting, "?");
        var gate = new WriteGate(VerifiedProtocolRegistry.Empty());
        var d = gate.Evaluate(p, Hw.Iface(path: "sim://x"));
        Assert.False(d.Allowed); Assert.Contains(d.Reasons, r => r.Contains("каталог"));
    }

    [Fact]
    public void CorruptOrIncompleteRegistryFailsClosed()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "v.json");
        File.WriteAllText(path, "{ this is not json");
        var reg = VerifiedProtocolRegistry.Load(path);
        Assert.Empty(reg.Entries); Assert.NotEmpty(reg.LoadProblems);
        var e = LedEntry(); e.Evidence = "";
        Assert.Empty(RegistryWith(e).Entries);
        Assert.Empty(VerifiedProtocolRegistry.Load(Path.Combine(d.Path, "missing.json")).Entries);
    }

    [Fact]
    public void ANewGateIsNeverArmedOnItsOwn()
    {
        var g1 = new WriteGate(VerifiedProtocolRegistry.Empty()); g1.Arm();
        Assert.True(g1.Armed);
        Assert.False(new WriteGate(VerifiedProtocolRegistry.Empty()).Armed);
        g1.Disarm(); Assert.False(g1.Armed);
    }

    [Fact]
    public void DocumentedExampleFileHasTheCorrectFormat()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KortyKeyboardStudio.sln"))) dir = dir.Parent;
        var reg = VerifiedProtocolRegistry.Load(Path.Combine(dir!.FullName, "docs", "verified-protocols.example.json"));
        Assert.Empty(reg.LoadProblems);
        var e = Assert.Single(reg.Entries);
        Assert.Equal(0x0C45, e.VendorId); Assert.Equal(0x8009, e.ProductId); Assert.Equal(0xFF13, e.UsagePage); Assert.Equal(0x18, e.CommandId); Assert.Equal("rgb", e.Stage);
        // The shipped data directory never contains such a file.
        Assert.Empty(VerifiedProtocolRegistry.Empty().Entries);
    }

    [Fact]
    public void ShippedRegistryIsEmpty() => Assert.Empty(VerifiedProtocolRegistry.Empty().Entries);
}

public class CommandServiceTests
{
    private static (DeviceCommandService svc, RecordingTransport tx, PhysicalDevice dev, WriteGate gate) Setup(bool simulated = true, bool control = true)
    {
        var sim = new SimulatedKeyboard(ProtocolFamily.AaFrame) { WirelessControlAvailable = control };
        var topo = DeviceClassifier.Classify(sim.Enumerate());
        var gate = new WriteGate(VerifiedProtocolRegistry.Empty());
        var tx = new RecordingTransport();
        return (new DeviceCommandService(gate, tx, new DiagLog()), tx, topo.Usb!, gate);
    }

    [Fact]
    public async Task SimulatedUsbAcceptsLedPacketThroughGate()
    {
        var (svc, tx, dev, _) = Setup();
        var r = await svc.SendAsync(new[] { AaFrameBuilder.SetLedEffect(new RgbSettings()) }, dev, null, default);
        Assert.True(r.Success, r.Message);
        Assert.Single(tx.Writes);
        Assert.True(tx.Writes[0].Target.IsSimulated);
    }

    [Fact]
    public async Task NothingIsSentWhenAnyPacketIsBlocked()
    {
        var (svc, tx, dev, _) = Setup();
        var good = AaFrameBuilder.SetLedEffect(new RgbSettings());
        var reset = new ProtocolPacket(ProtocolFamily.AaFrame, 0x0F, "SET_FACTORY_RESET", ReportKind.Output, TargetRole.Control, 0, AaFrameBuilder.Frame(0x0F, 1), CommandRisk.Destructive, "x");
        var r = await svc.SendAsync(new[] { good, reset }, dev, null, default);
        Assert.False(r.Success);
        Assert.Empty(tx.Writes);   // all-or-nothing: even the harmless first packet must not leave
    }

    [Fact]
    public async Task NoDeviceMeansNoSend()
    {
        var (svc, tx, _, _) = Setup();
        var r = await svc.SendAsync(new[] { AaFrameBuilder.SetLedEffect(new RgbSettings()) }, null, null, default);
        Assert.False(r.Success); Assert.Empty(tx.Writes);
    }

    [Fact]
    public async Task TransportFailureStopsTheSequence()
    {
        var (svc, tx, dev, _) = Setup();
        tx.FailOnIndex = 1;
        var plan = TftUploadPlanner.Plan(Px.Anim((1, 2, 3, 50), (3, 2, 1, 50)), TftEncodingOptions.For(ProtocolFamily.AaFrame));
        var r = await svc.SendAsync(plan.Packets, dev, null, default);
        Assert.False(r.Success); Assert.Equal(1, r.Sent); Assert.Single(tx.Writes);
    }

    [Fact]
    public void ReceiverWithoutMatchingInterfaceIsBlockedWithReason()
    {
        var sim = new SimulatedKeyboard(ProtocolFamily.AaFrame) { WirelessControlAvailable = false };
        sim.SetConnection(SimConnection.Wireless24);
        var topo = DeviceClassifier.Classify(sim.Enumerate());
        var svc = new DeviceCommandService(new WriteGate(VerifiedProtocolRegistry.Empty()), new RecordingTransport(), new DiagLog());
        var pre = svc.Check(new[] { AaFrameBuilder.SetLedEffect(new RgbSettings()) }, topo.Wireless);
        Assert.False(pre.AllAllowed);
        Assert.Contains(pre.DistinctBlockers, b => b.Contains("нет vendor-интерфейса"));
    }

    [Fact]
    public void AmbiguousInterfacesAreNotGuessed()
    {
        var a = Hw.Iface(path: "sim://a", outLen: 65, mi: 1); var b = Hw.Iface(path: "sim://b", outLen: 65, mi: 2);
        var dev = DeviceClassifier.Classify(new[] { a, b }).Devices.Single();
        var (t, err) = DeviceCommandService.Resolve(dev, AaFrameBuilder.SetLedEffect(new RgbSettings()));
        Assert.Null(t); Assert.Contains("неоднозначно", err);
    }

    [Fact]
    public void RealHardwareIsBlockedEvenWhenEverythingElseMatches()
    {
        var real = DeviceClassifier.Classify(new[] { Hw.Iface() }).Devices.Single();
        var gate = new WriteGate(VerifiedProtocolRegistry.Empty()); gate.Arm();
        var svc = new DeviceCommandService(gate, new RecordingTransport(), new DiagLog());
        var pre = svc.Check(new[] { AaFrameBuilder.SetLedEffect(new RgbSettings()) }, real);
        Assert.False(pre.AllAllowed);
    }
}

public class SimulationRoundTripTests
{
    [Theory]
    [InlineData(ProtocolFamily.AaFrame)]
    [InlineData(ProtocolFamily.Feature04)]
    public async Task TftAndLedSurviveTheFullPipeline(ProtocolFamily family)
    {
        var sim = new SimulatedKeyboard(family);
        var topo = DeviceClassifier.Classify(sim.Enumerate());
        var svc = new DeviceCommandService(new WriteGate(VerifiedProtocolRegistry.Empty()), sim, new DiagLog());
        var opts = TftEncodingOptions.For(family);

        var anim = Px.Anim((255, 0, 0, 100), (0, 255, 0, 200), (0, 0, 255, 300));
        var plan = TftUploadPlanner.Plan(anim, opts);
        var r = await svc.SendAsync(plan.Packets, topo.Usb, null, default);
        Assert.True(r.Success, r.Message);
        Assert.NotNull(sim.Screen); Assert.False(sim.ScreenIsBlack);
        Assert.Equal(3, sim.Screen!.Frames.Count);
        Assert.Equal(new[] { 100, 200, 300 }, sim.Screen.Frames.Select(f => f.DelayMs));
        Assert.InRange(Px.At(sim.Screen.Frames[0].Bgra, 10, 10).r, 245, 255);
        Assert.InRange(Px.At(sim.Screen.Frames[2].Bgra, 10, 10).b, 245, 255);

        var rgb = new RgbSettings { Mode = 7, Primary = new(1, 2, 3), Brightness = 5, Speed = 1, Direction = 2 };
        IReadOnlyList<ProtocolPacket> led = family == ProtocolFamily.AaFrame ? new[] { AaFrameBuilder.SetLedEffect(rgb) } : Feature04Builder.LightingSequence(rgb);
        var r2 = await svc.SendAsync(led, topo.Usb, null, default);
        Assert.True(r2.Success, r2.Message);
        Assert.Equal(7, sim.Rgb!.Mode); Assert.Equal(new RgbColor(1, 2, 3), sim.Rgb.Primary);
        Assert.Equal(5, sim.Rgb.Brightness); Assert.Equal(2, sim.Rgb.Direction);
    }

    [Fact]
    public async Task WrongMagicIsRejectedByTheSimulatedFirmware()
    {
        var sim = new SimulatedKeyboard(ProtocolFamily.AaFrame);
        var dev = DeviceClassifier.Classify(sim.Enumerate()).Usb;
        var svc = new DeviceCommandService(new WriteGate(VerifiedProtocolRegistry.Empty()), sim, new DiagLog());
        var plan = TftUploadPlanner.Plan(Px.Anim((1, 1, 1, 50)), TftEncodingOptions.For(ProtocolFamily.AaFrame));
        var broken = plan.Packets.Select(p => { var b = (byte[])p.Body.Clone(); b[6] = 0x4F; b[7] = 0x06; return p with { Body = b }; }).ToList(); // 0x064F bug
        var r = await svc.SendAsync(broken, dev, null, default);
        Assert.False(r.Success); Assert.Contains("magic", r.Message);
    }

    [Fact]
    public async Task ModeSwitchHypothesisScenarioBlanksScreenAndRestoreWorksOnlyWhereControlExists()
    {
        var sim = new SimulatedKeyboard(ProtocolFamily.AaFrame) { ResetScreenOnSwitch = true, WirelessControlAvailable = false };
        var svc = new DeviceCommandService(new WriteGate(VerifiedProtocolRegistry.Empty()), sim, new DiagLog());
        var plan = TftUploadPlanner.Plan(Px.Anim((1, 200, 1, 50)), TftEncodingOptions.For(ProtocolFamily.AaFrame));
        Assert.True((await svc.SendAsync(plan.Packets, DeviceClassifier.Classify(sim.Enumerate()).Usb, null, default)).Success);
        sim.SetConnection(SimConnection.Wireless24);
        Assert.True(sim.ScreenIsBlack); Assert.Null(sim.Screen);
        // 2.4 GHz without control path: restore is blocked with an explanation.
        var wl = DeviceClassifier.Classify(sim.Enumerate()).Wireless;
        Assert.False(svc.Check(plan.Packets, wl).AllAllowed);
        // Back on cable: restore passes.
        sim.SetConnection(SimConnection.Usb);
        Assert.True((await svc.SendAsync(plan.Packets, DeviceClassifier.Classify(sim.Enumerate()).Usb, null, default)).Success);
        Assert.False(sim.ScreenIsBlack);
    }
}
