using Korty.Core.Model;
using Korty.Core.Protocol;
using Korty.Core.Tft;
using Xunit;

namespace Korty.Tests;

public class Rgb565Tests
{
    [Fact]
    public void PrimaryColoursPackLittleEndian()
    {
        Assert.Equal(new byte[] { 0x00, 0xF8 }, Rgb565.FromBgra(Px.Solid(255, 0, 0))[..2]);
        Assert.Equal(new byte[] { 0xE0, 0x07 }, Rgb565.FromBgra(Px.Solid(0, 255, 0))[..2]);
        Assert.Equal(new byte[] { 0x1F, 0x00 }, Rgb565.FromBgra(Px.Solid(0, 0, 255))[..2]);
        Assert.Equal(Rgb565.FrameBytes, Rgb565.FromBgra(Px.Solid(1, 2, 3)).Length);
        Assert.Equal(32768, Rgb565.FrameBytes);
    }

    [Fact]
    public void RoundTripErrorIsWithinQuantisationStep()
    {
        var src = Px.Solid(200, 100, 50);
        var back = Rgb565.ToBgra(Rgb565.FromBgra(src));
        var (r, g, b, a) = Px.At(back, 5, 5);
        Assert.InRange(Math.Abs(r - 200), 0, 8); Assert.InRange(Math.Abs(g - 100), 0, 4); Assert.InRange(Math.Abs(b - 50), 0, 8);
        Assert.Equal(255, a);
    }

    [Fact]
    public void DitheringKeepsLengthAndAveragesCloserThanPlain()
    {
        // A flat mid-grey that RGB565 cannot represent exactly: dithering must keep the mean closer to the target.
        var src = Px.Solid(130, 130, 130);
        double Mean(byte[] rgb565) { var bgra = Rgb565.ToBgra(rgb565); double s = 0; for (int i = 2; i < bgra.Length; i += 4) s += bgra[i]; return s / (bgra.Length / 4); }
        var plain = Rgb565.FromBgra(src, false); var dith = Rgb565.FromBgra(src, true);
        Assert.Equal(plain.Length, dith.Length);
        Assert.True(Math.Abs(Mean(dith) - 130) <= Math.Abs(Mean(plain) - 130) + 0.01);
    }

    [Fact]
    public void TransparentPixelsFlattenOverBlack()
    {
        var px = new byte[TftAnimation.FrameBytesBgra]; // fully transparent
        Assert.All(Rgb565.FromBgra(px), v => Assert.Equal(0, v));
    }
}

public class PacketBuilderTests
{
    [Fact]
    public void AaLedPacketMatchesDocumentedLayout()
    {
        var s = new RgbSettings { Mode = 5, Primary = new(10, 20, 30), Secondary = new(1, 2, 3), ColorMode = 1, Brightness = 4, Speed = 2, Direction = 3 };
        var p = AaFrameBuilder.SetLedEffect(s);
        Assert.Equal(64, p.Body.Length); Assert.Equal(65, p.WireLength); Assert.Equal(0, p.ReportId);
        Assert.Equal(0xAA, p.Body[0]); Assert.Equal(0x23, p.Body[1]); Assert.Equal(16, p.Body[2]);
        var payload = p.Body[8..24];
        Assert.Equal(new byte[] { 5, 10, 20, 30, 0xFF, 1, 2, 3, 1, 4, 2, 3, 0, 0, 0xAA, 0x55 }, payload);
        Assert.All(p.Body[24..], b => Assert.Equal(0, b));
        Assert.NotNull(p.Assumption);
    }

    [Theory]
    [InlineData(20, 3, 3, 0)] [InlineData(-1, 3, 3, 0)] [InlineData(0, 6, 3, 0)] [InlineData(0, 3, 6, 0)] [InlineData(0, 3, 3, 4)]
    public void OutOfRangeSettingsAreRejected(int mode, int br, int sp, int dir)
    {
        var s = new RgbSettings { Mode = mode, Brightness = br, Speed = sp, Direction = dir };
        Assert.Throws<ArgumentOutOfRangeException>(() => AaFrameBuilder.SetLedEffect(s));
        Assert.Throws<ArgumentOutOfRangeException>(() => Feature04Builder.ModeData(s));
    }

    [Fact]
    public void Feature04LightingSequenceMatchesTheReferenceOrderAndDefaults()
    {
        var rgb = new RgbSettings { Mode = 2, Primary = new(9, 8, 7), Brightness = 5, Speed = 1, Direction = 2 };
        var seq = Feature04Builder.LightingSequence(rgb);   // defaults: SAVE byte8 = 0, no FINISH (as in the reference)
        Assert.Equal(new[] { "START", "MODE_CFG", "MODE_DATA", "SAVE" }, seq.Select(p => p.CommandName));
        Assert.All(seq, p => { Assert.Equal(ReportKind.Feature, p.Kind); Assert.Equal(64, p.Body.Length); Assert.Equal(65, p.WireLength); Assert.Equal(Stages.Rgb, p.Stage); });
        Assert.Equal(new byte[] { 0x04, 0x18, 0x00 }, seq[0].Body[..3]); Assert.Equal(1, seq[0].Body[8]);
        Assert.Equal(new byte[] { 0x04, 0x13, 0x00 }, seq[1].Body[..3]);
        var d = seq[2].Body;
        Assert.Equal(new byte[] { 2, 9, 8, 7 }, d[..4]); Assert.Equal(5, d[9]); Assert.Equal(1, d[10]); Assert.Equal(2, d[11]);
        Assert.Equal(0x55, d[14]); Assert.Equal(0xAA, d[15]); // trailer order differs from the AA family
        Assert.Equal(new byte[] { 0x04, 0x02 }, seq[3].Body[..2]); Assert.Equal(0, seq[3].Body[8]);

        var full = Feature04Builder.LightingSequence(rgb, new Feature04Options { SaveFlag = 1, FinishAfterLighting = true });
        Assert.Equal(new[] { "START", "MODE_CFG", "MODE_DATA", "SAVE", "FINISH" }, full.Select(p => p.CommandName));
        Assert.Equal(1, full[3].Body[8]); Assert.Equal(new byte[] { 0x04, 0xF0 }, full[4].Body[..2]);
    }

    [Fact]
    public void ImageCfgEncodesChunkCountLittleEndian()
    {
        var p = Feature04Builder.ImageCfg(0x03, 0x0102 + 0x100);
        Assert.Equal(new byte[] { 0x04, 0x72, 0x03 }, p.Body[..3]);
        Assert.Equal(0x02, p.Body[8]); Assert.Equal(0x02, p.Body[9]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Feature04Builder.ImageCfg(3, 0));
    }

    [Fact]
    public void CatalogMarksDestructiveAndUnknownAsNeverSendable()
    {
        Assert.True(CommandCatalog.Find(ProtocolFamily.AaFrame, 0x0F)!.NeverSendable);   // factory reset
        Assert.True(CommandCatalog.Find(ProtocolFamily.AaFrame, 0x22)!.NeverSendable);   // set key
        Assert.True(CommandCatalog.Find(ProtocolFamily.AaFrame, 0x21)!.NeverSendable);   // set game mode (report rate/debounce)
        Assert.Equal(CommandRisk.Unknown, CommandCatalog.RiskOf(ProtocolFamily.AaFrame, 0x99));
        Assert.All(CommandCatalog.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Source)));
        // The catalog never claims hardware verification in this release.
        Assert.DoesNotContain(CommandCatalog.All, c => c.Evidence == EvidenceLevel.HardwareVerified);
    }
}

public class TftPayloadTests
{
    [Fact]
    public void HeaderPaddingAndFrameLayoutAa()
    {
        var anim = Px.Anim((255, 0, 0, 100), (0, 255, 0, 250), (0, 0, 255, 5000));
        var o = TftEncodingOptions.For(ProtocolFamily.AaFrame);
        var payload = TftPayloadBuilder.Build(anim, o);
        Assert.Equal(0, payload.Length % 4096);
        Assert.Equal(3, payload[0]);
        Assert.Equal(20, payload[1]);   // 100 ms / 5
        Assert.Equal(50, payload[2]);   // 250 ms / 5
        Assert.Equal(255, payload[3]);  // 5000/5 = 1000 → clamped to 255 (max 1275 ms)
        Assert.Equal(0xFF, payload[4]); Assert.Equal(0xFF, payload[255]);
        Assert.Equal(new byte[] { 0x00, 0xF8 }, payload[256..258]);                 // frame 0 red
        Assert.Equal(new byte[] { 0xE0, 0x07 }, payload[(256 + 32768)..(256 + 32768 + 2)]);
        Assert.Equal(0xFF, payload[256 + 3 * 32768]);                               // padding
        Assert.Equal(256 + 3 * 32768, 98560);
        Assert.Equal(98560 / 4096 + 1, payload.Length / 4096);                      // rounded up
    }

    [Fact]
    public void DelayUnitDiffersBetweenFamilies()
    {
        var anim = Px.Anim((1, 1, 1, 100), (2, 2, 2, 100));
        Assert.Equal(20, TftPayloadBuilder.Build(anim, TftEncodingOptions.For(ProtocolFamily.AaFrame))[1]);
        Assert.Equal(50, TftPayloadBuilder.Build(anim, TftEncodingOptions.For(ProtocolFamily.Feature04))[1]);
    }

    [Fact]
    public void StillImageHasZeroDelayAndOneFrame()
    {
        var p = TftPayloadBuilder.Build(Px.Anim((9, 9, 9, 777)), TftEncodingOptions.For(ProtocolFamily.Feature04));
        Assert.Equal(1, p[0]); Assert.Equal(0, p[1]);
    }

    [Fact]
    public void ZeroTerminatorOptionAddsTerminatorByte()
    {
        var o = TftEncodingOptions.For(ProtocolFamily.AaFrame) with { ZeroTerminator = true };
        var p = TftPayloadBuilder.Build(Px.Anim((1, 1, 1, 50), (1, 1, 1, 50)), o);
        Assert.Equal(0, p[3]); Assert.Equal(0xFF, p[4]);
    }

    [Fact]
    public void TooManyFramesAreDecimatedPreservingDuration()
    {
        var frames = Enumerable.Range(0, 300).Select(i => new TftFrame(Px.Solid((byte)i, 0, 0), 40)).ToList();
        var anim = new TftAnimation(frames);
        var d = anim.Decimate(100);
        Assert.Equal(100, d.Frames.Count);
        Assert.Equal(anim.TotalDurationMs, d.TotalDurationMs);
        var payload = TftPayloadBuilder.Build(anim, TftEncodingOptions.For(ProtocolFamily.AaFrame) with { MaxFrames = 255 });
        Assert.Equal(255, payload[0]);
    }

    [Fact]
    public void DecodeIsInverseOfBuild()
    {
        var anim = Px.Anim((250, 10, 10, 100), (10, 250, 10, 200));
        var o = TftEncodingOptions.For(ProtocolFamily.AaFrame);
        var back = TftPayloadBuilder.Decode(TftPayloadBuilder.Build(anim, o), o.DelayUnitMs);
        Assert.Equal(2, back.Frames.Count);
        Assert.Equal(100, back.Frames[0].DelayMs); Assert.Equal(200, back.Frames[1].DelayMs);
        var (r, g, _, _) = Px.At(back.Frames[1].Bgra, 64, 64);
        Assert.InRange(r, 0, 12); Assert.InRange(g, 245, 255);
    }

    [Fact]
    public void AaUploadPlanChunksHaveCorrectHeaders()
    {
        var plan = TftUploadPlanner.Plan(Px.Anim((255, 0, 0, 100), (0, 255, 0, 100)), TftEncodingOptions.For(ProtocolFamily.AaFrame));
        Assert.Equal(plan.PayloadBytes / 4096, plan.DataChunks);
        Assert.Equal(plan.DataChunks, plan.Packets.Count);
        for (int i = 0; i < plan.Packets.Count; i++)
        {
            var b = plan.Packets[i].Body;
            Assert.Equal(4104, b.Length);
            Assert.Equal(new byte[] { 0xAA, 0x50 }, b[..2]);
            Assert.Equal(i, b[2] | (b[3] << 8));
            Assert.Equal(plan.DataChunks, b[4] | (b[5] << 8));
            Assert.Equal(new byte[] { 0x50, 0x06 }, b[6..8]);   // magic 0x0650 LE (0x064F bug documented upstream)
            Assert.Equal(TargetRole.Data, plan.Packets[i].Target);
        }
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public void Feature04UploadPlanSequence()
    {
        var plan = TftUploadPlanner.Plan(Px.Anim((255, 0, 0, 100)), TftEncodingOptions.For(ProtocolFamily.Feature04));
        Assert.Equal("START", plan.Packets[0].CommandName);
        Assert.Equal("IMAGE_CFG", plan.Packets[1].CommandName);
        Assert.Equal(plan.DataChunks, plan.Packets[1].Body[8] | (plan.Packets[1].Body[9] << 8));
        Assert.All(plan.Packets.Skip(2).Take(plan.DataChunks), p => { Assert.Equal(4096, p.Body.Length); Assert.Equal(TargetRole.Data, p.Target); });
        Assert.Equal("SAVE", plan.Packets[^1].CommandName);
        var gohv = TftUploadPlanner.Plan(Px.Anim((255, 0, 0, 100)), TftEncodingOptions.For(ProtocolFamily.Feature04) with { GohvVariant = true });
        Assert.Equal("FINISH", gohv.Packets[^1].CommandName); Assert.Equal(0x02, gohv.Packets[1].Body[2]); Assert.Equal(1, gohv.Packets[0].Body[8]);
    }

    [Fact]
    public void BuildingAPlanSendsNothingAndDoesNotMutateInput()
    {
        var anim = Px.Anim((1, 2, 3, 100));
        var copy = anim.Frames[0].Bgra.ToArray();
        _ = TftUploadPlanner.Plan(anim, TftEncodingOptions.For(ProtocolFamily.AaFrame));
        Assert.Equal(copy, anim.Frames[0].Bgra);
    }
}
