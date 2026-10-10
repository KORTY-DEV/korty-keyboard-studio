using System.Text.Json;
using Korty.Core.Diagnostics;
using Korty.Core.Discovery;
using Korty.Core.Hid;
using Korty.Core.Model;
using Korty.Core.Protocol;
using Korty.Core.Safety;
using Korty.Core.Simulation;
using Korty.Core.Storage;
using Xunit;

namespace Korty.Tests;

public class DeviceClassifierTests
{
    [Fact]
    public void OtherVendorsAndForeignSonixDevicesAreNeverActionable()
    {
        var sim = new SimulatedKeyboard { IncludeDecoys = true };
        var topo = DeviceClassifier.Classify(sim.Enumerate());
        Assert.Equal(1, topo.IgnoredOtherVendorInterfaces);                         // the mouse
        Assert.Contains(topo.Devices, d => d.Kind == ConnectionKind.UnknownSonix && !d.IsActionable);   // webcam with the same VID
        Assert.Single(topo.Devices, d => d.IsActionable);
    }

    [Fact]
    public void CableAndReceiverAreSeparateDevicesEvenWhenBothPresent()
    {
        var sim = new SimulatedKeyboard();
        sim.SetConnection(SimConnection.UsbAndReceiver);
        var topo = DeviceClassifier.Classify(sim.Enumerate());
        Assert.NotNull(topo.Usb); Assert.NotNull(topo.Wireless);
        Assert.NotEqual(topo.Usb!.Key, topo.Wireless!.Key);
        Assert.All(topo.Usb.Interfaces, i => Assert.Equal(0x8009, i.Info.ProductId));
        Assert.All(topo.Wireless.Interfaces, i => Assert.Equal(0xFDFD, i.Info.ProductId));
    }

    [Fact]
    public void BootloaderIsRecognisedAndNotActionable()
    {
        var topo = DeviceClassifier.Classify(new[] { Hw.Iface(pid: 0x7140) });
        Assert.True(topo.HasBootloader); Assert.False(topo.Devices.Single().IsActionable);
    }

    [Fact]
    public void SystemInputInterfacesAreNotVendorInterfaces()
    {
        var sim = new SimulatedKeyboard();
        var usb = DeviceClassifier.Classify(sim.Enumerate()).Usb!;
        Assert.Contains(usb.Interfaces, i => i.Role == InterfaceRole.SystemInput);
        Assert.True(usb.VendorInterfaceCount >= 3);
    }

    [Fact]
    public void EmptyInputGivesEmptyTopology() =>
        Assert.Empty(DeviceClassifier.Classify(Array.Empty<HidInterfaceInfo>()).Devices);
}

public class ConnectionMonitorTests
{
    [Fact]
    public async Task ArrivalIsReportedImmediatelyRemovalOnlyAfterConfirmation()
    {
        var sim = new SimulatedKeyboard { IncludeDecoys = false };
        sim.SetConnection(SimConnection.None);
        using var mon = new ConnectionMonitor(sim);
        var events = new List<ConnectionChange>();
        mon.TopologyChanged += (_, e) => { lock (events) events.AddRange(e.Changes); };

        await mon.PollOnceAsync();
        Assert.Empty(events);

        sim.SetConnection(SimConnection.Usb);
        await mon.PollOnceAsync();
        Assert.Contains(events, c => c.Kind == ConnectionChangeKind.Arrived && c.Device == ConnectionKind.UsbCable);

        sim.SetConnection(SimConnection.None);
        await mon.PollOnceAsync();                         // one miss: still considered present (re-enumeration glitch)
        Assert.DoesNotContain(events, c => c.Kind == ConnectionChangeKind.Removed);
        Assert.NotNull(mon.Current.Usb);
        await mon.PollOnceAsync();                         // second miss: gone
        Assert.Contains(events, c => c.Kind == ConnectionChangeKind.Removed);
        Assert.Null(mon.Current.Usb);
    }

    [Fact]
    public async Task TransientDisappearanceDoesNotFireRemoval()
    {
        var sim = new SimulatedKeyboard { IncludeDecoys = false };
        using var mon = new ConnectionMonitor(sim);
        int removed = 0; mon.TopologyChanged += (_, e) => removed += e.Changes.Count(c => c.Kind == ConnectionChangeKind.Removed);
        await mon.PollOnceAsync();
        sim.SetConnection(SimConnection.None); await mon.PollOnceAsync();
        sim.SetConnection(SimConnection.Usb); await mon.PollOnceAsync();
        await mon.PollOnceAsync();
        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task EnumerationFailureNeitherCrashesNorRemovesDevices()
    {
        var fake = new FakeEnumerator { Items = { Hw.Iface() } };
        using var mon = new ConnectionMonitor(fake);
        await mon.PollOnceAsync();
        Assert.NotNull(mon.Current.Usb);
        fake.Throw = new InvalidOperationException("SetupAPI exploded");
        for (int i = 0; i < 4; i++) await mon.PollOnceAsync();
        Assert.NotNull(mon.Current.Usb);
        Assert.Contains("exploded", mon.LastError);
    }

    [Fact]
    public async Task ChangedInterfaceSetIsReported()
    {
        var sim = new SimulatedKeyboard { IncludeDecoys = false };
        sim.SetConnection(SimConnection.Wireless24);
        using var mon = new ConnectionMonitor(sim);
        var changes = new List<ConnectionChange>(); mon.TopologyChanged += (_, e) => changes.AddRange(e.Changes);
        await mon.PollOnceAsync();
        sim.WirelessControlAvailable = true;      // receiver now exposes different vendor interfaces
        await mon.PollOnceAsync();
        Assert.Contains(changes, c => c.Kind == ConnectionChangeKind.Changed);
    }
}

public class DifferentialTests
{
    [Fact]
    public void PidPresentInOnlyOneSnapshotIsAttributedToThatSituation()
    {
        var sim = new SimulatedKeyboard { IncludeDecoys = false };
        sim.SetConnection(SimConnection.Usb);
        var cable = DeviceSnapshot.Capture("только кабель", sim.Enumerate());
        sim.SetConnection(SimConnection.Wireless24);
        var rx = DeviceSnapshot.Capture("только донгл", sim.Enumerate());
        var diff = DifferentialAnalyzer.Compare(cable, rx);
        Assert.Contains(diff.Conclusions, c => c.Contains("0C45:8009") && c.Contains("только кабель"));
        Assert.Contains(diff.Conclusions, c => c.Contains("0C45:FDFD") && c.Contains("только донгл"));
        Assert.NotEmpty(diff.OnlyInA); Assert.NotEmpty(diff.OnlyInB);
    }

    [Fact]
    public void IdenticalSnapshotsAreFlagged()
    {
        var sim = new SimulatedKeyboard();
        var a = DeviceSnapshot.Capture("a", sim.Enumerate()); var b = DeviceSnapshot.Capture("b", sim.Enumerate());
        Assert.Contains(DifferentialAnalyzer.Compare(a, b).Conclusions, c => c.Contains("идентичны"));
    }

    [Fact]
    public void SnapshotSerialisationRoundTrips()
    {
        var s = DeviceSnapshot.Capture("x", new SimulatedKeyboard().Enumerate());
        var back = DifferentialAnalyzer.Deserialize(DifferentialAnalyzer.Serialize(s))!;
        Assert.Equal(s.Interfaces.Count, back.Interfaces.Count);
        Assert.Equal(s.Interfaces[0].Fingerprint, back.Interfaces[0].Fingerprint);
    }

    [Fact]
    public void SnapshotContainsNoDevicePathsOrInstanceIds()
    {
        var json = DifferentialAnalyzer.Serialize(DeviceSnapshot.Capture("x", new SimulatedKeyboard().Enumerate()));
        Assert.DoesNotContain("sim://", json); Assert.DoesNotContain("hid#", json, StringComparison.OrdinalIgnoreCase);
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void RedactionRemovesPathsHexTokensAndUserNames()
    {
        var r = DiagLog.Redact(@"open \\?\hid#vid_0c45&pid_8009&mi_01#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030} by C:\Users\korty\x serial 0123456789ABCDEF0123");
        Assert.DoesNotContain("7&1a2b", r); Assert.DoesNotContain("alice", r); Assert.DoesNotContain("0123456789ABCDEF", r);
        Assert.Contains("<hid-path>", r);
    }

    [Fact]
    public void ReportContainsNoSerialsAndStatesZeroDiscoveryWrites()
    {
        var sim = new SimulatedKeyboard();
        var topo = DeviceClassifier.Classify(sim.Enumerate());
        var log = new DiagLog(); log.Info("t", @"path \\?\hid#vid_0c45&pid_8009#abc");
        var json = DiagnosticReport.Build(topo, Array.Empty<DeviceSnapshot>(), false, 0, log);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("Safety").GetProperty("HidWritesPerformedByDiscovery").GetInt32());
        Assert.DoesNotContain("sim://", json); Assert.DoesNotContain("hid#", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RingBufferIsBoundedAndFileLogRotates()
    {
        using var d = new TempDir();
        var log = new DiagLog(d.Path);
        for (int i = 0; i < 2500; i++) log.Info("x", "line " + i);
        Assert.True(log.Snapshot().Count <= 2000);
        Assert.True(File.Exists(Path.Combine(d.Path, "studio.log")));
    }

    [Fact]
    public void LoggingNeverThrowsOnUnwritableDirectory()
    {
        var log = new DiagLog(@"Z:\definitely\not\here\" + Guid.NewGuid());
        log.Error("x", "still fine");
        Assert.Single(log.Snapshot());
    }
}

public class StorageTests
{
    [Fact]
    public void ProfilesPersistAndKeepUsbAndWirelessIndependent()
    {
        using var d = new TempDir();
        var store = new ProfileStore(d.Path);
        var p = store.Load();
        p.Get(StudioProfile.UsbKey).Rgb = new RgbSettings { Mode = 4, Primary = new(1, 2, 3) };
        p.Get(StudioProfile.WirelessKey).Rgb = new RgbSettings { Mode = 9, Primary = new(9, 9, 9) };
        p.Get(StudioProfile.UsbKey).DesignId = new string('a', 32);
        p.Settings.SimulationMode = true; p.Settings.SimulatedFamily = ProtocolFamily.Feature04;
        store.Save(p);
        var again = new ProfileStore(d.Path).Load();
        Assert.Equal(4, again.Get("usb").Rgb.Mode); Assert.Equal(9, again.Get("wireless24").Rgb.Mode);
        Assert.Equal(new RgbColor(1, 2, 3), again.Get("usb").Rgb.Primary);
        Assert.Equal(new string('a', 32), again.Get("usb").DesignId); Assert.Null(again.Get("wireless24").DesignId);
        Assert.True(again.Settings.SimulationMode); Assert.Equal(ProtocolFamily.Feature04, again.Settings.SimulatedFamily);
    }

    [Fact]
    public void CorruptProfileIsQuarantinedNotOverwritten()
    {
        using var d = new TempDir();
        File.WriteAllText(Path.Combine(d.Path, "profile.json"), "{{{ broken");
        var store = new ProfileStore(d.Path);
        var p = store.Load();
        Assert.NotNull(p); Assert.NotNull(store.LastLoadWarning);
        Assert.Single(Directory.GetFiles(d.Path, "profile.json.corrupt-*"));
    }

    [Fact]
    public void HandEditedOutOfRangeValuesAreClampedOnLoad()
    {
        using var d = new TempDir();
        File.WriteAllText(Path.Combine(d.Path, "profile.json"), """{"Connections":{"usb":{"Rgb":{"Mode":99,"Brightness":-4,"Speed":77,"Direction":9}}}}""");
        var rgb = new ProfileStore(d.Path).Load().Get("usb").Rgb;
        Assert.Equal(19, rgb.Mode); Assert.Equal(0, rgb.Brightness); Assert.Equal(5, rgb.Speed); Assert.Equal(3, rgb.Direction);
        rgb.Validate();
    }

    [Fact]
    public void AtomicWriteLeavesNoTempFilesAndSurvivesRepeatedSaves()
    {
        using var d = new TempDir();
        var store = new ProfileStore(d.Path);
        var p = store.Load();
        for (int i = 0; i < 20; i++) { p.Settings.LastDesignId = i.ToString(); store.Save(p); }
        Assert.Empty(Directory.GetFiles(d.Path, "*.tmp"));
        Assert.Equal("19", store.Load().Settings.LastDesignId);
    }

    [Fact]
    public void ConcurrentSavesDoNotCorruptTheProfile()
    {
        using var d = new TempDir();
        var store = new ProfileStore(d.Path);
        var p = store.Load();
        Parallel.For(0, 30, i => store.Save(p));
        Assert.Null(new ProfileStore(d.Path).Load().Settings.LastDesignId);
        Assert.Empty(Directory.GetFiles(d.Path, "*.corrupt-*"));
    }

    [Fact]
    public void DesignLibraryRoundTripsPolymorphicLayers()
    {
        using var d = new TempDir();
        var lib = new DesignLibrary(d.Path);
        var c = new DisplayComposition { Name = "Тест" };
        c.Layers.Add(new TextLayer { Name = "t", Text = "Привет", FontSize = 22, Color = new(1, 2, 3), X = 4.5 });
        var src = Path.Combine(d.Path, "pic.png"); File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        var asset = lib.ImportAsset(c.Id, src);
        c.Layers.Add(new ImageLayer { Name = "i", Asset = asset, Scale = 1.5, Fit = ImageFit.Contain });
        lib.Save(c, new byte[] { 9 });
        var back = lib.Load(c.Id);
        Assert.Equal("Тест", back.Name);
        var t = Assert.IsType<TextLayer>(back.Layers[0]); Assert.Equal("Привет", t.Text); Assert.Equal(4.5, t.X); Assert.Equal(new RgbColor(1, 2, 3), t.Color);
        var i = Assert.IsType<ImageLayer>(back.Layers[1]); Assert.Equal(1.5, i.Scale); Assert.Equal(ImageFit.Contain, i.Fit);
        Assert.True(File.Exists(lib.AssetPath(c.Id, asset)));
        Assert.Single(lib.List());
    }

    [Fact]
    public void LibraryRejectsPathTraversalAndBadAssets()
    {
        using var d = new TempDir();
        var lib = new DesignLibrary(d.Path);
        Assert.Throws<ArgumentException>(() => lib.Load(@"..\..\evil"));
        Assert.Throws<ArgumentException>(() => lib.AssetPath(new string('a', 32), @"..\..\x.png"));
        Assert.Throws<ArgumentException>(() => lib.Delete("../x"));
        var exe = Path.Combine(d.Path, "x.exe"); File.WriteAllBytes(exe, new byte[4]);
        Assert.Throws<NotSupportedException>(() => lib.ImportAsset(new string('b', 32), exe));
        Assert.False(LibraryPaths.IsValidAsset("a.png")); Assert.True(LibraryPaths.IsValidAsset(new string('0', 32) + ".gif"));
    }

    [Fact]
    public void TamperedCompositionWithTraversalAssetIsDroppedOnLoad()
    {
        using var d = new TempDir();
        var lib = new DesignLibrary(d.Path);
        var id = new string('c', 32);
        Directory.CreateDirectory(Path.Combine(d.Path, "designs", id));
        File.WriteAllText(Path.Combine(d.Path, "designs", id, "composition.json"),
            """{"Id":"cccccccccccccccccccccccccccccccc","Name":"x","Layers":[{"type":"image","Asset":"..\\..\\secret.png"},{"type":"text","Text":"ok","FontSize":1e9,"X":1e400}]}""");
        var c = lib.Load(id);
        Assert.Single(c.Layers);
        Assert.Equal(128, ((TextLayer)c.Layers[0]).FontSize);
    }

    [Fact]
    public void DuplicateCopiesAssetsWithNewIdAndListSkipsBrokenDesigns()
    {
        using var d = new TempDir();
        var lib = new DesignLibrary(d.Path);
        var c = new DisplayComposition();
        var src = Path.Combine(d.Path, "p.png"); File.WriteAllBytes(src, new byte[8]);
        c.Layers.Add(new ImageLayer { Asset = lib.ImportAsset(c.Id, src) });
        lib.Save(c);
        var copy = lib.Duplicate(c.Id);
        Assert.NotEqual(c.Id, copy.Id);
        Assert.True(File.Exists(lib.AssetPath(copy.Id, ((ImageLayer)copy.Layers[0]).Asset)));
        Directory.CreateDirectory(Path.Combine(d.Path, "designs", new string('d', 32)));
        File.WriteAllText(Path.Combine(d.Path, "designs", new string('d', 32), "composition.json"), "garbage");
        Assert.Equal(2, lib.List().Count);
        lib.Delete(c.Id);
        Assert.Single(lib.List());
    }

    [Fact]
    public void OversizedImportIsRefused()
    {
        using var d = new TempDir();
        var big = Path.Combine(d.Path, "big.png");
        using (var fs = File.Create(big)) fs.SetLength(DesignLibrary.MaxAssetBytes + 1);
        Assert.Throws<InvalidDataException>(() => new DesignLibrary(d.Path).ImportAsset(new string('e', 32), big));
    }
}

public class RestorePlannerTests
{
    [Fact]
    public async Task PlanIsBlockedWithoutVerifiedPathAndExecutableInSimulation()
    {
        var sim = new SimulatedKeyboard();
        var svc = new DeviceCommandService(new WriteGate(VerifiedProtocolRegistry.Empty()), sim, new DiagLog());
        var planner = new Korty.Core.Recovery.RestorePlanner(svc);
        var usb = DeviceClassifier.Classify(sim.Enumerate()).Usb;
        var rgb = new[] { AaFrameBuilder.SetLedEffect(new RgbSettings()) };
        var tft = Korty.Core.Tft.TftUploadPlanner.Plan(Px.Anim((5, 6, 7, 50)), Korty.Core.Tft.TftEncodingOptions.For(ProtocolFamily.AaFrame)).Packets;
        var plan = planner.Plan("usb", new ConnectionProfile { DesignId = new string('a', 32) }, usb, rgb, tft, "Тест");
        Assert.True(plan.CanExecute);
        var res = await planner.ExecuteAsync(plan, usb, null, default);
        Assert.True(res.Success, res.Message);
        Assert.NotNull(sim.Screen); Assert.NotNull(sim.Rgb);

        // Same plan against a real (non-simulated) descriptor is blocked.
        var real = DeviceClassifier.Classify(new[] { Hw.Iface() }).Devices.Single();
        var blocked = planner.Plan("usb", new ConnectionProfile(), real, rgb, null, null);
        Assert.False(blocked.CanExecute);
        Assert.Contains(blocked.Steps, s => s.Status == Korty.Core.Recovery.StepStatus.Blocked);
        Assert.False((await planner.ExecuteAsync(blocked, real, null, default)).Success);
    }

    [Fact]
    public void NoDeviceGivesBlockedPlan()
    {
        var svc = new DeviceCommandService(new WriteGate(VerifiedProtocolRegistry.Empty()), new RecordingTransport(), new DiagLog());
        var plan = new Korty.Core.Recovery.RestorePlanner(svc).Plan("usb", new ConnectionProfile(), null, Array.Empty<ProtocolPacket>(), null, null);
        Assert.False(plan.CanExecute);
    }
}
