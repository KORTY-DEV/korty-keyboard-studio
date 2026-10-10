using System.Security.Cryptography;
using System.Text.Json;
using Korty.Core.Diagnostics;
using Korty.Core.Discovery;
using Korty.Core.Model;
using Korty.Core.Protocol;
using Korty.Core.Safety;
using Korty.Core.Storage;
using Korty.Core.Tft;
using Xunit;

namespace Korty.Tests;

public static class Repo
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KortyKeyboardStudio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }
    public static JsonDocument Golden() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests", "Korty.Tests", "Golden", "kabelklaus-golden.json")));
}

/// <summary>Recording stand-in for the OS HID layer. Behaves like a keyboard that answers every feature read and every chunk.</summary>
public sealed class FakeHidIo : IHidDeviceIo
{
    public sealed record Op(string Kind, string Path, byte[] Data, int Arg = 0);
    public List<Op> Ops { get; } = new();
    public bool FailGetFeature { get; set; }
    public bool NoAck { get; set; }
    public int FailWriteAtChunk { get; set; } = -1;
    public bool FailSetFeature { get; set; }
    private int _chunks;

    public void SetFeature(string path, byte[] b) { if (FailSetFeature) throw new IOException("set failed"); Ops.Add(new("SetFeature", path, (byte[])b.Clone())); }
    public byte[]? GetFeature(string path, int len) { Ops.Add(new("GetFeature", path, Array.Empty<byte>(), len)); if (FailGetFeature) throw new IOException("get failed"); var r = new byte[len]; r[1] = 0x04; return r; }
    public void WriteOutput(string path, byte[] b)
    {
        if (_chunks++ == FailWriteAtChunk) throw new IOException("write failed");
        Ops.Add(new("WriteOutput", path, (byte[])b.Clone()));
    }
    public byte[]? ReadInput(string path, int len, int timeoutMs) { Ops.Add(new("ReadInput", path, Array.Empty<byte>(), len * 100000 + timeoutMs)); return NoAck ? null : new byte[len]; }
    public void FlushInput(string path) => Ops.Add(new("FlushInput", path, Array.Empty<byte>()));
    public void Dispose() { }
}

/// <summary>Everything needed to run the real software stack (service → gate → HidTransportCore) against a fake device with REAL (non-simulated) descriptors.</summary>
public sealed class RealShapedRig : IDisposable
{
    public const string CtrlPath = @"\\?\hid#vid_0c45&pid_8009&mi_01&col05#fake-ctrl";
    public const string DataPath = @"\\?\hid#vid_0c45&pid_8009&mi_02#fake-data";
    public TempDir Dir { get; } = new();
    public FakeHidIo Io { get; } = new();
    public WriteGate Gate { get; private set; }
    public DeviceCommandService Service { get; }
    public PhysicalDevice Device { get; }
    public string RegistryPath => Path.Combine(Dir.Path, "verified-protocols.json");
    public DiagLog Log { get; } = new();

    public RealShapedRig(bool wrongControlLength = false)
    {
        var ifaces = new[]
        {
            new HidInterfaceInfo { Path = @"\\?\hid#vid_0c45&pid_8009&mi_00#kbd", VendorId = 0x0C45, ProductId = 0x8009, UsagePage = 1, Usage = 6, InputReportLength = 9, OutputReportLength = 2, ContainerId = "c", InterfaceNumber = 0 },
            new HidInterfaceInfo { Path = CtrlPath, VendorId = 0x0C45, ProductId = 0x8009, UsagePage = 0xFF13, Usage = 1, InputReportLength = 65, FeatureReportLength = wrongControlLength ? 64 : 65, ContainerId = "c", InterfaceNumber = 1, CollectionNumber = 5 },
            new HidInterfaceInfo { Path = DataPath, VendorId = 0x0C45, ProductId = 0x8009, UsagePage = 0xFF68, Usage = 0x61, InputReportLength = 65, OutputReportLength = 4097, ContainerId = "c", InterfaceNumber = 2 },
        };
        Device = DeviceClassifier.Classify(ifaces).Usb!;
        Gate = new WriteGate(VerifiedProtocolRegistry.Load(RegistryPath));
        Service = new DeviceCommandService(Gate, new HidTransportCore(Io), Log);
    }

    public void ActivateAndArm(params string[] stages)
    {
        foreach (var s in stages) Gate.ReplaceRegistry(ActivationStages.Activate(RegistryPath, s));
        Gate.Arm(); Assert.True(Gate.Armed);
    }

    public void Dispose() => Dir.Dispose();
}

public class GoldenReferenceTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void GoldenFileIsTiedToTheExactReferenceSource()
    {
        using var g = Repo.Golden();
        Assert.Equal("52e8215cb4e6cd97b9a1cea9d9d2fbe75902f4234970e54a3230741da1ed0844", g.RootElement.GetProperty("appJsSha256").GetString());
        Assert.Equal(0, g.RootElement.GetProperty("framing").GetProperty("reportId").GetInt32());
    }

    [Fact]
    public void LightingSequencesAreByteAndTimingIdenticalToTheReference()
    {
        using var g = Repo.Golden();
        foreach (var c in g.RootElement.GetProperty("lighting").EnumerateArray())
        {
            var pr = c.GetProperty("params");
            var rgb = new RgbSettings
            {
                Mode = pr.GetProperty("mode").GetInt32(), Primary = new((byte)pr.GetProperty("r").GetInt32(), (byte)pr.GetProperty("g").GetInt32(), (byte)pr.GetProperty("b").GetInt32()),
                ColorMode = pr.GetProperty("rainbow").GetBoolean() ? 1 : 0, Brightness = pr.GetProperty("bright").GetInt32(), Speed = pr.GetProperty("speed").GetInt32(), Direction = pr.GetProperty("dir").GetInt32(),
            };
            var seq = Feature04Builder.LightingSequence(rgb, new Feature04Options { SaveFlag = (byte)pr.GetProperty("saveByte8").GetInt32(), FinishAfterLighting = pr.GetProperty("finish").GetBoolean() });
            var gold = c.GetProperty("seq").EnumerateArray().ToList();
            Assert.Equal(gold.Count, seq.Count);
            for (int i = 0; i < gold.Count; i++)
            {
                Assert.Equal(gold[i].GetProperty("label").GetString(), seq[i].CommandName);
                Assert.Equal(gold[i].GetProperty("hex").GetString(), Hex(seq[i].Body));
                bool handshake = gold[i].GetProperty("handshake").GetBoolean();
                Assert.Equal(handshake, seq[i].HandshakeAfterMs != null);
                if (handshake) Assert.Equal(gold[i].GetProperty("sleepBeforeHandshakeMs").GetInt32(), seq[i].HandshakeAfterMs);
                Assert.Equal(gold[i].GetProperty("settleMs").GetInt32(), seq[i].SettleMs);
            }
        }
    }

    private static TftAnimation Frames(IReadOnlyList<int> delays) => new(delays.Select((d, i) =>
    {
        var px = new byte[TftAnimation.FrameBytesBgra];
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
        {
            int o = (y * 128 + x) * 4;
            px[o + 2] = (byte)((x * 2 + i * 10) & 255); px[o + 1] = (byte)((y * 2 + i * 3) & 255); px[o] = (byte)(((x + y) * 3 + i * 7) & 255); px[o + 3] = 255;
        }
        return new TftFrame(px, d);
    }).ToList());

    [Fact]
    public void ImagePayloadsHeadersAndSequencesAreIdenticalToTheReference()
    {
        using var g = Repo.Golden();
        foreach (var img in g.RootElement.GetProperty("images").EnumerateArray())
        {
            var delays = img.GetProperty("delaysMs").EnumerateArray().Select(e => e.GetInt32()).ToList();
            bool gohv = img.GetProperty("variant").GetString() == "gohv";
            var o = TftEncodingOptions.For(ProtocolFamily.Feature04) with { GohvVariant = gohv };
            var anim = Frames(delays);
            var payload = TftPayloadBuilder.Build(anim, o);
            Assert.Equal(img.GetProperty("payloadLength").GetInt32(), payload.Length);
            Assert.Equal(img.GetProperty("headerHex").GetString(), Hex(payload[..256]));    // includes the JS half-up rounding of delays/2
            Assert.Equal(img.GetProperty("payloadSha256").GetString(), Sha(payload));

            var plan = TftUploadPlanner.Plan(anim, o);
            int chunks = img.GetProperty("totalChunks").GetInt32();
            Assert.Equal(chunks, plan.DataChunks);
            Assert.Equal(chunks + 3, plan.Packets.Count);
            var before = img.GetProperty("before").EnumerateArray().ToList();
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(before[i].GetProperty("label").GetString(), plan.Packets[i].CommandName);
                Assert.Equal(before[i].GetProperty("hex").GetString(), Hex(plan.Packets[i].Body));
                Assert.Equal(before[i].GetProperty("sleepBeforeHandshakeMs").GetInt32(), plan.Packets[i].HandshakeAfterMs);
            }
            var last = img.GetProperty("after")[0];
            Assert.Equal(last.GetProperty("label").GetString(), plan.Packets[^1].CommandName);
            Assert.Equal(last.GetProperty("hex").GetString(), Hex(plan.Packets[^1].Body));
            Assert.Equal(0, plan.Packets[^1].HandshakeAfterMs);

            var chunkPackets = plan.Packets.Skip(2).Take(chunks).ToList();
            Assert.All(chunkPackets, c => { Assert.Equal(4096, c.Body.Length); Assert.Equal(300, c.AckWaitMs); Assert.Equal(ReportKind.Output, c.Kind); Assert.Equal(TargetRole.Data, c.Target); });
            Assert.Equal(img.GetProperty("payloadSha256").GetString(), Sha(chunkPackets.SelectMany(c => c.Body).ToArray()));
            Assert.Equal(img.GetProperty("lastChunkSha256").GetString(), Sha(chunkPackets[^1].Body));
            Assert.Equal(Stages.ForFrames(delays.Count), plan.Stage);
            Assert.All(plan.Packets, p => Assert.Equal(plan.Stage, p.Stage));
        }
    }

    [Fact]
    public void HalfMillisecondDelaysRoundHalfUpLikeJavaScript()
    {
        // 53 ms / 2 = 26.5 → JS Math.round gives 27 (C# default banker's rounding would give 26)
        var anim = new TftAnimation(new[] { new TftFrame(new byte[TftAnimation.FrameBytesBgra], 53), new TftFrame(new byte[TftAnimation.FrameBytesBgra], 1) });
        var p = TftPayloadBuilder.Build(anim, TftEncodingOptions.For(ProtocolFamily.Feature04));
        Assert.Equal(27, p[1]); Assert.Equal(1, p[2]);    // 1 ms → clamped to the minimum 1
    }
}

public class WindowsFramingConformanceTests
{
    private static byte[] Wire(string hex64) => new byte[] { 0 }.Concat(Convert.FromHexString(hex64)).ToArray();

    [Fact]
    public async Task RgbSequenceReachesTheKeyboardAsTheExactWindowsBuffersOfTheReference()
    {
        using var rig = new RealShapedRig();
        rig.ActivateAndArm(Stages.Rgb);
        using var g = Repo.Golden();
        var c = g.RootElement.GetProperty("lighting")[0];
        var pr = c.GetProperty("params");
        var rgb = new RgbSettings { Mode = 1, Primary = new(255, 69, 0), Brightness = 5, Speed = 3, Direction = 0 };
        var r = await rig.Service.SendAsync(Feature04Builder.LightingSequence(rgb), rig.Device, null, default);
        Assert.True(r.Success, r.Message);

        var gold = c.GetProperty("seq").EnumerateArray().ToList();
        var expected = new List<(string kind, byte[]? data)>();
        foreach (var step in gold)
        {
            expected.Add(("SetFeature", Wire(step.GetProperty("hex").GetString()!)));
            if (step.GetProperty("handshake").GetBoolean()) expected.Add(("GetFeature", null));
        }
        Assert.Equal(expected.Count, rig.Io.Ops.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var op = rig.Io.Ops[i];
            Assert.Equal(expected[i].kind, op.Kind);
            Assert.Equal(RealShapedRig.CtrlPath, op.Path);                        // only the FF13 control interface is touched
            if (op.Kind == "SetFeature") { Assert.Equal(65, op.Data.Length); Assert.Equal(0, op.Data[0]); Assert.Equal(expected[i].data, op.Data); }
            else Assert.Equal(65, op.Arg);                                         // HidD_GetFeature buffer = report id + 64
        }
        Assert.DoesNotContain(rig.Io.Ops, o => o.Kind is "WriteOutput" or "ReadInput");
    }

    [Fact]
    public async Task PngUploadUsesControlFeaturesAndDataOutputReportsWithAcks()
    {
        using var rig = new RealShapedRig();
        rig.ActivateAndArm(Stages.Rgb, Stages.Png);
        var anim = new TftAnimation(new[] { new TftFrame(Px.Solid(200, 30, 90), 0) });
        var plan = TftUploadPlanner.Plan(anim, TftEncodingOptions.For(ProtocolFamily.Feature04));
        Assert.Equal(Stages.Png, plan.Stage);
        var r = await rig.Service.SendAsync(plan.Packets, rig.Device, null, default);
        Assert.True(r.Success, r.Message);
        Assert.Equal(plan.DataChunks, r.AcksExpected); Assert.Equal(0, r.AcksMissed);

        var writes = rig.Io.Ops.Where(o => o.Kind == "WriteOutput").ToList();
        Assert.Equal(plan.DataChunks, writes.Count);
        Assert.All(writes, w => { Assert.Equal(RealShapedRig.DataPath, w.Path); Assert.Equal(4097, w.Data.Length); Assert.Equal(0, w.Data[0]); });
        var payload = TftPayloadBuilder.Build(anim, TftEncodingOptions.For(ProtocolFamily.Feature04));
        Assert.Equal(payload, writes.SelectMany(w => w.Data.Skip(1)).ToArray());   // chunk bytes = payload, report id stripped

        // order: START(+get) IMAGE_CFG(+get) then per chunk Flush→Write→Read(300 ms, 65 B), then SAVE(+get)
        var kinds = rig.Io.Ops.Select(o => o.Kind).ToList();
        Assert.Equal(new[] { "SetFeature", "GetFeature", "SetFeature", "GetFeature" }, kinds.Take(4));
        Assert.Equal(new[] { "FlushInput", "WriteOutput", "ReadInput" }, kinds.Skip(4).Take(3));
        Assert.Equal(new[] { "SetFeature", "GetFeature" }, kinds.TakeLast(2));
        Assert.Equal(65 * 100000 + 300, rig.Io.Ops.First(o => o.Kind == "ReadInput").Arg);
        Assert.All(rig.Io.Ops.Where(o => o.Kind is "SetFeature" or "GetFeature"), o => Assert.Equal(RealShapedRig.CtrlPath, o.Path));
        Assert.Equal(0x04, rig.Io.Ops[0].Data[1]); Assert.Equal(0x18, rig.Io.Ops[0].Data[2]);      // START
        Assert.Equal(0x72, rig.Io.Ops[2].Data[2]); Assert.Equal(0x03, rig.Io.Ops[2].Data[3]);      // IMAGE_CFG sub 3 (AKS075)
        Assert.Equal(0x02, rig.Io.Ops[^2].Data[2]);                                                // SAVE
    }

    [Fact]
    public async Task StagesAreStrictlySeparated()
    {
        using var rig = new RealShapedRig();
        rig.ActivateAndArm(Stages.Rgb);                                    // only RGB
        var still = TftUploadPlanner.Plan(new TftAnimation(new[] { new TftFrame(Px.Solid(1, 2, 3), 0) }), TftEncodingOptions.For(ProtocolFamily.Feature04));
        var pre = rig.Service.Check(still.Packets, rig.Device);
        Assert.False(pre.AllAllowed);
        Assert.Contains(pre.DistinctBlockers, b => b.Contains("PNG"));
        Assert.Empty(rig.Io.Ops);
        var rgbOk = rig.Service.Check(Feature04Builder.LightingSequence(new RgbSettings()), rig.Device);
        Assert.True(rgbOk.AllAllowed);

        rig.Gate.ReplaceRegistry(ActivationStages.Activate(rig.RegistryPath, Stages.Png));   // RGB + PNG
        var anim = TftUploadPlanner.Plan(new TftAnimation(new[] { new TftFrame(Px.Solid(1, 2, 3), 50), new TftFrame(Px.Solid(3, 2, 1), 50) }), TftEncodingOptions.For(ProtocolFamily.Feature04));
        Assert.Equal(Stages.Gif, anim.Stage);
        var gifPre = rig.Service.Check(anim.Packets, rig.Device);
        Assert.False(gifPre.AllAllowed); Assert.Contains(gifPre.DistinctBlockers, b => b.Contains("GIF"));
        Assert.True(rig.Service.Check(still.Packets, rig.Device).AllAllowed);

        rig.Gate.ReplaceRegistry(ActivationStages.Activate(rig.RegistryPath, Stages.Gif));
        Assert.True(rig.Service.Check(anim.Packets, rig.Device).AllAllowed);
    }

    [Fact]
    public void ActivationIsOrderedPersistentAndCascadesOnDeactivation()
    {
        using var rig = new RealShapedRig();
        Assert.NotNull(ActivationStages.CannotActivate(Stages.Png, rig.Gate.VerifiedRegistry));    // rgb first
        Assert.Throws<InvalidOperationException>(() => ActivationStages.Activate(rig.RegistryPath, Stages.Gif));
        var reg = ActivationStages.Activate(rig.RegistryPath, Stages.Rgb);
        Assert.Empty(reg.LoadProblems);
        Assert.Equal(new[] { Stages.Rgb }, ActivationStages.ActiveStages(reg));
        Assert.NotNull(ActivationStages.CannotActivate(Stages.Rgb, reg));                          // already active
        reg = ActivationStages.Activate(rig.RegistryPath, Stages.Png);
        reg = ActivationStages.Activate(rig.RegistryPath, Stages.Gif);
        reg = ActivationStages.Activate(rig.RegistryPath, Stages.Settings);
        Assert.Equal(Stages.All, ActivationStages.ActiveStages(VerifiedProtocolRegistry.Load(rig.RegistryPath)));   // survives a "restart"
        reg = ActivationStages.Deactivate(rig.RegistryPath, Stages.Png);                           // drops png AND gif, settings only needs rgb
        Assert.Equal(new[] { Stages.Rgb, Stages.Settings }, ActivationStages.ActiveStages(reg));
        Assert.Empty(ActivationStages.ActiveStages(ActivationStages.Deactivate(rig.RegistryPath, Stages.Rgb)));
    }

    [Fact]
    public void ActivatedEntriesNeverCoverDestructiveOrUnknownCommands()
    {
        foreach (var stage in Stages.All)
            foreach (var e in ActivationStages.EntriesFor(stage, "2026-01-01"))
            {
                var cmd = CommandCatalog.Find(e.Family, e.CommandId)!;
                Assert.False(cmd.NeverSendable, cmd.Name);
                Assert.Equal(0x0C45, e.VendorId); Assert.Equal(0x8009, e.ProductId);
                Assert.True((e.UsagePage == 0xFF13 && e.Kind == ReportKind.Feature && e.ReportLength == 65) || (e.UsagePage == 0xFF68 && e.Kind == ReportKind.Output && e.ReportLength == 4097));
            }
        // RGB stage must not contain the image command or the data interface.
        Assert.DoesNotContain(ActivationStages.EntriesFor(Stages.Rgb, "x"), e => e.CommandId == 0x72 || e.UsagePage == 0xFF68);
    }

    [Fact]
    public async Task WritingStillNeedsSessionArmingEvenWithActivatedStages()
    {
        using var rig = new RealShapedRig();
        rig.Gate.ReplaceRegistry(ActivationStages.Activate(rig.RegistryPath, Stages.Rgb));   // activated but NOT armed
        var r = await rig.Service.SendAsync(Feature04Builder.LightingSequence(new RgbSettings()), rig.Device, null, default);
        Assert.False(r.Success); Assert.Contains("запись в клавиатуру выключена", r.Message);
        Assert.Empty(rig.Io.Ops);
        rig.Gate.Disarm();
    }

    [Fact]
    public async Task MissingHandshakeIsNonFatalAndMissingAcksAreCountedNotHidden()
    {
        using var rig = new RealShapedRig();
        rig.ActivateAndArm(Stages.Rgb, Stages.Png);
        rig.Io.FailGetFeature = true; rig.Io.NoAck = true;
        var plan = TftUploadPlanner.Plan(new TftAnimation(new[] { new TftFrame(Px.Solid(9, 9, 9), 0) }), TftEncodingOptions.For(ProtocolFamily.Feature04));
        var r = await rig.Service.SendAsync(plan.Packets, rig.Device, null, default);
        Assert.True(r.Success, r.Message);                         // as in the reference: warnings, not aborts
        Assert.Equal(plan.DataChunks, r.AcksMissed);
        Assert.Contains("ACK без ответа", r.Message);
    }

    [Fact]
    public async Task WriteFailureAbortsImmediatelyAndReportsTheChunk()
    {
        using var rig = new RealShapedRig();
        rig.ActivateAndArm(Stages.Rgb, Stages.Png);
        rig.Io.FailWriteAtChunk = 3;
        var plan = TftUploadPlanner.Plan(new TftAnimation(new[] { new TftFrame(Px.Solid(9, 9, 9), 0) }), TftEncodingOptions.For(ProtocolFamily.Feature04));
        var r = await rig.Service.SendAsync(plan.Packets, rig.Device, null, default);
        Assert.False(r.Success); Assert.Equal(2 + 3, r.Sent);      // START, CFG, 3 chunks sent
        Assert.Contains("write failed", r.Message);
        Assert.DoesNotContain(rig.Io.Ops, o => o.Kind == "SetFeature" && o.Data[2] == 0x02);     // SAVE was NOT sent after a failed upload
    }

    [Fact]
    public async Task SetFeatureFailureAbortsTheSequence()
    {
        using var rig = new RealShapedRig();
        rig.ActivateAndArm(Stages.Rgb);
        rig.Io.FailSetFeature = true;
        var r = await rig.Service.SendAsync(Feature04Builder.LightingSequence(new RgbSettings()), rig.Device, null, default);
        Assert.False(r.Success); Assert.Equal(0, r.Sent); Assert.Empty(rig.Io.Ops);
    }

    [Fact]
    public async Task WrongDescriptorLengthsBlockEverythingBeforeTheFirstByte()
    {
        using var rig = new RealShapedRig(wrongControlLength: true);   // control feature report 64 instead of 65
        rig.ActivateAndArm(Stages.Rgb);
        var r = await rig.Service.SendAsync(Feature04Builder.LightingSequence(new RgbSettings()), rig.Device, null, default);
        Assert.False(r.Success); Assert.Empty(rig.Io.Ops);
        Assert.Contains("FF13", r.Message);
    }

    [Fact]
    public async Task TransportRefusesForeignOrSimulatedInterfacesEvenIfGivenAToken()
    {
        // Tokens can only be minted by the gate; mint legitimate ones for a simulated and a foreign interface and hand them to the real transport.
        var gate = new WriteGate(VerifiedProtocolRegistry.Empty());
        var pkt = Feature04Builder.Start(1) with { Stage = Stages.Rgb };
        var sim = Hw.Iface(path: "sim://x", page: 0xFF13, outLen: 0, featLen: 65);
        var decision = gate.Evaluate(pkt, sim);
        Assert.True(decision.Allowed);
        var tx = new HidTransportCore(new FakeHidIo());
        var res = await tx.SendAsync(decision.Approved!, default);
        Assert.False(res.Success); Assert.Contains("simulated", res.Error);
    }

    [Fact]
    public void ResolveUsesTheDocumentedUsagePagePerRole()
    {
        using var rig = new RealShapedRig();
        var (ctrl, e1) = DeviceCommandService.Resolve(rig.Device, Feature04Builder.Start(1));
        Assert.Equal(RealShapedRig.CtrlPath, ctrl!.Path);
        var chunk = new ProtocolPacket(ProtocolFamily.Feature04, 0x72, "IMAGE_DATA", ReportKind.Output, TargetRole.Data, 0, new byte[4096], CommandRisk.PersistentSetting, "x");
        var (data, e2) = DeviceCommandService.Resolve(rig.Device, chunk);
        Assert.Equal(RealShapedRig.DataPath, data!.Path);
        Assert.Null(e1); Assert.Null(e2);
    }
}

public class ProfileMigrationTests
{
    [Fact]
    public void V1ProfilesMoveToTheWorkingFamilyAndKeepTheirData()
    {
        using var d = new TempDir();
        File.WriteAllText(Path.Combine(d.Path, "profile.json"),
            """{"SchemaVersion":1,"Settings":{"SimulationMode":true,"SimulatedFamily":"AaFrame","LastDesignId":"abc"},"Connections":{"usb":{"Rgb":{"Mode":5}}}}""");
        var p = new ProfileStore(d.Path).Load();
        Assert.Equal(StudioProfile.CurrentSchema, p.SchemaVersion);
        Assert.Equal(ProtocolFamily.Feature04, p.Settings.SimulatedFamily);
        Assert.True(p.Settings.SimulationMode); Assert.Equal("abc", p.Settings.LastDesignId); Assert.Equal(5, p.Get("usb").Rgb.Mode);
        Assert.Equal(0, p.Settings.SaveFlag); Assert.False(p.Settings.FinishAfterLighting); Assert.False(p.Settings.GohvImage);
    }

    [Fact]
    public void FreshProfilesUseTheReferenceDefaults()
    {
        using var d = new TempDir();
        var p = new ProfileStore(d.Path).Load();
        Assert.Equal(ProtocolFamily.Feature04, p.Settings.SimulatedFamily);
        Assert.Equal(0, p.Settings.SaveFlag); Assert.False(p.Settings.FinishAfterLighting); Assert.False(p.Settings.GohvImage);
    }

    [Fact]
    public void ModeNamesMatchTheReferenceAndDirectionAppliesToFourEffects()
    {
        Assert.Equal(20, RgbModes.Names.Count); Assert.Equal(20, RgbModes.NamesEn.Count);
        Assert.Equal("Static", RgbModes.NamesEn[1]); Assert.Equal("Shuttle", RgbModes.NamesEn[19]);
        Assert.Equal(new[] { 0x0A, 0x0B, 0x10, 0x12 }, Enumerable.Range(0, 20).Where(RgbModes.UsesDirection).ToArray());
    }
}

public class RgbPreviewAndColourTests
{
    private static readonly IReadOnlyList<RgbPreviewEngine.Press> None = Array.Empty<RgbPreviewEngine.Press>();
    private static RgbSettings S(int mode, int br = 5, int dir = 0, int speed = 3, bool rainbow = false) =>
        new() { Mode = mode, Brightness = br, Direction = dir, Speed = speed, ColorMode = rainbow ? 1 : 0, Primary = new(200, 100, 50) };

    [Fact]
    public void OffAndZeroBrightnessAreDarkStaticScalesWithBrightness()
    {
        Assert.Equal((0, 0, 0), RgbPreviewEngine.Color(S(0), .5, .5, 1, None));
        Assert.Equal((0, 0, 0), RgbPreviewEngine.Color(S(1, br: 0), .5, .5, 1, None));
        Assert.Equal((200, 100, 50), RgbPreviewEngine.Color(S(1, 5), .3, .3, 9, None));
        Assert.Equal((120, 60, 30), RgbPreviewEngine.Color(S(1, 3), .3, .3, 9, None));
    }

    [Fact]
    public void EveryModeProducesValidColoursAtAnyTime()
    {
        for (int m = 0; m < 20; m++) for (double t = 0; t < 5; t += 0.37) foreach (var x in new[] { 0.0, .5, 1 })
            { var (r, g, b) = RgbPreviewEngine.Color(S(m), x, .5, t, new[] { new RgbPreviewEngine.Press(.5, .5, 0) }); Assert.InRange(r, 0, 255); }
    }

    [Fact]
    public void KeyOnReactsToPressesAndFadesKeyOffInverts()
    {
        var press = new[] { new RgbPreviewEngine.Press(.5, .5, 1.0) };
        int fresh = RgbPreviewEngine.Color(S(2), .5, .5, 1.05, press).r, old = RgbPreviewEngine.Color(S(2), .5, .5, 4.0, press).r, idle = RgbPreviewEngine.Color(S(2), .1, .5, 1.05, press).r;
        Assert.True(fresh > 150 && old < 40 && idle < 40);
        Assert.True(RgbPreviewEngine.Color(S(3), .5, .5, 1.05, press).r < 40 && RgbPreviewEngine.Color(S(3), .1, .5, 1.05, press).r > 150);
    }

    [Fact]
    public void RippleTravelsOutwardAndDirectionMirrorsScroll()
    {
        var press = new[] { new RgbPreviewEngine.Press(.5, .5, 0) };
        double Best(double x) => Enumerable.Range(0, 60).Max(i => RgbPreviewEngine.Color(S(15), x, .5, i * 0.03, press).r);
        Assert.True(Best(.5) > 100 && Best(.8) > 40);                               // wave reaches near and far keys
        double Peak(int dir, double x) => Enumerable.Range(0, 120).Max(i => RgbPreviewEngine.Color(S(10, dir: dir), x, .5, i * 0.05, None).r);
        Assert.True(Peak(0, .1) > 100 && Peak(3, .1) > 100);                        // both directions sweep the board
        int atT = RgbPreviewEngine.Color(S(10, dir: 0), .1, .5, 0.2, None).r, mirrored = RgbPreviewEngine.Color(S(10, dir: 3), .9, .5, 0.2, None).r;
        Assert.Equal(atT, mirrored);                                                // dir 3 is the mirror image of dir 0
    }

    [Fact]
    public void SpeedChangesTheTempo()
    {
        var a = RgbPreviewEngine.Color(S(7, speed: 0), .5, .5, 1.0, None); var b = RgbPreviewEngine.Color(S(7, speed: 5), .5, .5, 1.0, None);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HsvRoundTripsAndKeepsHueForGreys()
    {
        foreach (var c in new[] { (255, 0, 0), (0, 255, 128), (12, 34, 56), (255, 255, 255), (1, 2, 3) })
        {
            var (h, s, v) = RgbPreviewEngine.ToHsv((byte)c.Item1, (byte)c.Item2, (byte)c.Item3);
            var (r, g, b) = RgbPreviewEngine.Hsv(h, s, v);
            Assert.InRange(Math.Abs(r - c.Item1), 0, 1); Assert.InRange(Math.Abs(g - c.Item2), 0, 1); Assert.InRange(Math.Abs(b - c.Item3), 0, 1);
        }
        Assert.Equal(200, RgbPreviewEngine.ToHsv(50, 50, 50, keepHue: 200).h);
    }
}

public class ColourViewModelTests
{
    [Fact]
    public void HexRgbAndHsvStayInSyncAndCustomColoursPersist() => Sta.Run(() =>
    {
        using var dir = new TempDir();
        using (var host = new Korty.App.AppHost(dir.Path, true))
        {
            var vm = new Korty.App.ViewModels.RgbViewModel(host) { PrimaryHex = "#336699" };
            Assert.Equal(51, vm.Red); Assert.Equal(102, vm.Green); Assert.Equal(153, vm.Blue);
            Assert.InRange(vm.Hue, 209, 211); Assert.InRange(vm.Sat, 66, 67); Assert.InRange(vm.Val, 59, 61);
            vm.Hue = 0; vm.Sat = 100; vm.Val = 100;                                  // pure red via HSV
            Assert.Equal("#FF0000", vm.PrimaryHex); Assert.Equal(255, vm.Red); Assert.Equal(0, vm.Blue);
            vm.Red = 0; vm.Green = 255;                                              // back via RGB sliders
            Assert.Equal("#00FF00", vm.PrimaryHex); Assert.InRange(vm.Hue, 119, 121);
            vm.PrimaryHex = "#000000"; vm.Hue = 77;                                  // grey keeps the user's hue without exploding
            vm.PrimaryHex = "#7F00FF"; vm.SaveColorCommand.Execute(null); vm.PrimaryHex = "#102030"; vm.SaveColorCommand.Execute(null);
            Assert.Equal(new[] { "#102030", "#7F00FF" }, vm.CustomColors);
            vm.RemoveColorCommand.Execute("#102030");
        }
        using var again = new Korty.App.AppHost(dir.Path, true);
        Assert.Equal(new[] { "#7F00FF" }, again.Profile.Settings.CustomColors);
        Assert.Equal(new RgbColor(0x10, 0x20, 0x30), again.Profile.Get("usb").Rgb.Primary);   // RGB profile saved as well
    });

    [Fact]
    public void ArbitraryColoursReachThePacketUnchanged()
    {
        foreach (var hex in new[] { "#000001", "#FEDCBA", "#123456", "#FFFFFF" })
        {
            var c = RgbColor.Parse(hex);
            var pkt = Feature04Builder.LightingSequence(new RgbSettings { Primary = c })[2].Body;
            Assert.Equal(new[] { c.R, c.G, c.B }, pkt[1..4]);
        }
    }
}

public class ClockAndUserContentTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static byte[] Data(KeyboardSettings k) => Feature04Builder.SettingsData(k);

    [Fact]
    public void SettingsPacketLayoutMatchesTheVendorSoftware()
    {
        var d = Data(new KeyboardSettings { SleepLevel = 2, KeyResponseLevel = 3 });
        Assert.Equal(64, d.Length);
        Assert.Equal(new byte[] { 0x00, 0x01, 0, 0, 0, 1, 2, 0, 3 }, d[..9]);
        Assert.Equal(new byte[] { 0xAA, 0x55 }, d[62..]);
        Assert.All(d[9..62], x => Assert.Equal(0, x));
        var all = Data(new KeyboardSettings { SleepLevel = 3, KeyResponseLevel = 5, DisableAltTab = true, DisableAltF4 = true, DisableWinKey = true });
        Assert.Equal(new byte[] { 0x00, 0x01, 1, 1, 1, 1, 3, 0, 5 }, all[..9]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Data(new KeyboardSettings { KeyResponseLevel = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Data(new KeyboardSettings { KeyResponseLevel = 6 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Data(new KeyboardSettings { SleepLevel = 4 }));
    }

    [Fact]
    public void SettingsSequenceIsStartCfgDataSaveWithVendorTimings()
    {
        var seq = Feature04Builder.SettingsSequence(new KeyboardSettings());
        Assert.Equal(new[] { "START", "SETTINGS_CFG", "SETTINGS_DATA", "SAVE" }, seq.Select(p => p.CommandName).ToArray());
        Assert.Equal("0418", Hex(seq[0].Body[..2])); Assert.Equal(0, seq[0].Body[8]);
        Assert.Equal("041701", Hex(seq[1].Body[..3])); Assert.Equal(1, seq[1].Body[8]);
        Assert.Equal("0402", Hex(seq[3].Body[..2]));
        Assert.All(seq, p => Assert.Equal(Stages.Settings, p.Stage));
        Assert.Equal(100, seq[3].HandshakeAfterMs);
    }

    [Fact]
    public void FiveLevelsHaveDescriptionsAndTimings()
    {
        Assert.Equal(5, KeyboardSettings.Levels.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, KeyboardSettings.Levels.Select(l => l.Level).ToArray());
        Assert.All(KeyboardSettings.Levels, l => { Assert.NotEmpty(l.Title); Assert.NotEmpty(l.WiredMs); Assert.NotEmpty(l.Wireless24Ms); Assert.NotEmpty(l.BluetoothMs); });
    }

    [Fact]
    public async Task SettingsStageIsSeparateFromImagesAndNeedsRgbFirst()
    {
        using var rig = new RealShapedRig();
        Assert.NotNull(ActivationStages.CannotActivate(Stages.Settings, rig.Gate.VerifiedRegistry));
        rig.ActivateAndArm(Stages.Rgb);
        Assert.False(rig.Service.Check(Feature04Builder.SettingsSequence(new KeyboardSettings()), rig.Device).AllAllowed);   // not activated yet
        rig.Gate.ReplaceRegistry(ActivationStages.Activate(rig.RegistryPath, Stages.Settings));                              // independent of png/gif
        var r = await rig.Service.SendAsync(Feature04Builder.SettingsSequence(new KeyboardSettings { KeyResponseLevel = 2 }), rig.Device, null, default);
        Assert.True(r.Success, r.Message);
        Assert.All(rig.Io.Ops, o => Assert.Equal(RealShapedRig.CtrlPath, o.Path));                                          // control interface only
        Assert.False(rig.Service.Check(TftUploadPlanner.Plan(new TftAnimation(new[] { new TftFrame(Px.Solid(1, 1, 1), 0) }), TftEncodingOptions.For(ProtocolFamily.Feature04)).Packets, rig.Device).AllAllowed);
        var reg = ActivationStages.Deactivate(rig.RegistryPath, Stages.Rgb);                                                // base stage drops dependants
        Assert.Empty(ActivationStages.ActiveStages(reg));
    }

    [Fact]
    public void TimeTokensAreFlaggedAsNonAutonomousSnapshots() => Sta.Run(() =>
    {
        var c = new DisplayComposition();
        c.Layers.Add(new TextLayer { Text = "{time}" });
        var built = Korty.Rendering.CompositionRenderer.Build(c, _ => null);
        Assert.Contains(built.Warnings, w => w.Contains("снимок"));
        var plain = new DisplayComposition(); plain.Layers.Add(new TextLayer { Text = "KORTY" });
        Assert.DoesNotContain(Korty.Rendering.CompositionRenderer.Build(plain, _ => null).Warnings, w => w.Contains("снимок"));
    });

    [Fact]
    public void ShippedKortyAnimationsDecodeAndPlanAsGifUploads() => Sta.Run(() =>
    {
        var dir = Path.Combine(Repo.Root(), "assets", "korty-display");
        if (!Directory.Exists(dir)) return;                                           // optional user content
        foreach (var f in Directory.GetFiles(dir, "*.gif").Where(x => !x.Contains("PREVIEW")))
        {
            var img = Korty.Rendering.ImageDecoder.Decode(f);
            Assert.True(img.Frames.Count >= 1);
            var comp = new DisplayComposition { MaxFrames = 60 };
            comp.Layers.Add(new ImageLayer { Asset = "x", Fit = ImageFit.Cover });
            var built = Korty.Rendering.CompositionRenderer.Build(comp, _ => img);
            var plan = TftUploadPlanner.Plan(built.Animation, TftEncodingOptions.For(ProtocolFamily.Feature04));
            Assert.Equal(built.Animation.Frames.Count, plan.FrameCount);
            Assert.Equal(plan.DataChunks + 3, plan.Packets.Count);
            Assert.Equal(img.Frames.Count > 1 ? Stages.Gif : Stages.Png, plan.Stage);
            Assert.All(plan.Packets, p => Assert.True(p.WireLength == 65 || p.WireLength == 4097));
        }
    });
}


public class PublicReleaseFeatureTests
{
    [Fact]
    public void OneClickConsentActivatesEverythingWithoutTypingAndPersists() => Sta.Run(() =>
    {
        using var dir = new TempDir();
        using (var host = new Korty.App.AppHost(dir.Path, forceSimulation: false))
        {
            Assert.False(host.ControlEnabled);
            Assert.False(host.EnableControl(_ => false));                      // declined → nothing changes
            Assert.False(host.ControlEnabled); Assert.False(File.Exists(host.VerifiedRegistryPath));
            Assert.True(host.EnableControl(_ => true));
            Assert.True(host.ControlEnabled);
            Assert.Equal(Stages.All, host.ActiveStages);
            Assert.True(host.Gate.Armed);
        }
        using (var again = new Korty.App.AppHost(dir.Path, forceSimulation: false))   // consent survives restarts
        {
            Assert.True(again.ControlEnabled);
            again.DisableControl();
            Assert.False(again.ControlEnabled); Assert.Empty(again.ActiveStages);
        }
        using var third = new Korty.App.AppHost(dir.Path, forceSimulation: false);
        Assert.False(third.ControlEnabled);
    });

    [Fact]
    public void EnabledControlStillRefusesWrongDevicesAndBlockedCommands()
    {
        using var rig = new RealShapedRig(wrongControlLength: true);
        rig.Gate.ReplaceRegistry(ActivationStages.ActivateAll(rig.RegistryPath)); rig.Gate.Arm();
        Assert.False(rig.Service.Check(Feature04Builder.LightingSequence(new RgbSettings()), rig.Device).AllAllowed);   // descriptor mismatch
        var reset = new ProtocolPacket(ProtocolFamily.AaFrame, 0x0F, "SET_FACTORY_RESET", ReportKind.Output, TargetRole.Control, 0, AaFrameBuilder.Frame(0x0F, 1), CommandRisk.Destructive, "x") { Stage = Stages.Rgb };
        Assert.False(rig.Gate.Evaluate(reset, rig.Device.Interfaces[1].Info).Allowed);
    }

    [Fact]
    public void PresetsAreValidSettingsAndPreviewAsLit()
    {
        Assert.True(RgbPresets.All.Count >= 12);
        Assert.Equal(RgbPresets.All.Count, RgbPresets.All.Select(p => p.Name).Distinct().Count());
        foreach (var p in RgbPresets.All)
        {
            p.Settings.Validate();
            Feature04Builder.LightingSequence(p.Settings);                         // builds a valid packet sequence
            double sum = 0; for (double t = 0; t < 6; t += 0.2) { var (r, g, b) = RgbPreviewEngine.Color(p.Settings, .5, .5, t, new[] { new RgbPreviewEngine.Press(.5, .5, 0) }); sum += r + g + b; }
            Assert.True(sum > 0, p.Name);
        }
    }

    [Theory]
    [InlineData(BackgroundKind.Aurora)] [InlineData(BackgroundKind.Plasma)] [InlineData(BackgroundKind.Matrix)] [InlineData(BackgroundKind.Rings)]
    public void GeneratedBackgroundsAreValidDeterministicLoopsThatPlan(BackgroundKind kind)
    {
        var (a, b) = BackgroundGenerator.DefaultColors(kind);
        var anim = BackgroundGenerator.Generate(kind, a, b);
        Assert.Equal(12, anim.Frames.Count); Assert.All(anim.Frames, f => Assert.Equal(100, f.DelayMs));
        Assert.Equal(anim.Frames[3].Bgra, BackgroundGenerator.Generate(kind, a, b).Frames[3].Bgra);       // deterministic
        Assert.NotEqual(anim.Frames[0].Bgra, anim.Frames[4].Bgra);                                      // actually animated
        double Mean(byte[] p) { double s = 0; for (int i = 0; i < p.Length; i += 4) s += p[i] + p[i + 1] + p[i + 2]; return s / (p.Length / 4); }
        Assert.True(Mean(anim.Frames[0].Bgra) > 1);                                                     // not black
        var plan = TftUploadPlanner.Plan(anim, TftEncodingOptions.For(ProtocolFamily.Feature04));
        Assert.Equal(Stages.Gif, plan.Stage); Assert.Equal(12 * 32768 / 4096 + 1 + 3, plan.Packets.Count);   // 12 frames + header → 97 chunks + 3
    }

    [Fact]
    public void GeneratedGifSurvivesExportAndReimport() => Sta.Run(() =>
    {
        var (a, b) = BackgroundGenerator.DefaultColors(BackgroundKind.Plasma);
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "bg.gif");
        Korty.Rendering.GifExport.Save(BackgroundGenerator.Generate(BackgroundKind.Plasma, a, b), file);
        var img = Korty.Rendering.ImageDecoder.Decode(file);
        Assert.Equal(12, img.Frames.Count); Assert.Equal(128, img.Width);
        Assert.All(img.Frames, f => Assert.Equal(100, f.DelayMs));
    });

    [Fact]
    public void KeyboardSettingsPersistAndExampleGifIsBundled() => Sta.Run(() =>
    {
        using var dir = new TempDir();
        using (var host = new Korty.App.AppHost(dir.Path, true))
        {
            var vm = new Korty.App.ViewModels.KeyboardSettingsViewModel(host);
            Assert.Equal(5, vm.Levels.Count); Assert.Single(vm.Levels, l => l.Selected);
            vm.Levels[0].Choose.Execute(null); vm.SleepLevel = 3; vm.DisableAltTab = true;
            Assert.True(vm.Levels[0].Selected); Assert.False(vm.Levels[2].Selected);
        }
        using var again = new Korty.App.AppHost(dir.Path, true);
        Assert.Equal(1, again.Profile.Settings.Keyboard.KeyResponseLevel);
        Assert.Equal(3, again.Profile.Settings.Keyboard.SleepLevel);
        Assert.True(again.Profile.Settings.Keyboard.DisableAltTab); Assert.False(again.Profile.Settings.Keyboard.DisableWinKey);
        var gif = Path.Combine(AppContext.BaseDirectory, "examples", "korty-24frames.gif");
        Assert.True(File.Exists(gif));
        Assert.Equal(24, Korty.Rendering.ImageDecoder.Decode(gif).Frames.Count);
    });
}
