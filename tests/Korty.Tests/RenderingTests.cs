using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Korty.Core.Model;
using Korty.Core.Tft;
using Korty.Rendering;
using Xunit;

namespace Korty.Tests;

public class ImageDecoderTests
{
    [Fact]
    public void GifFramesHonourOffsetsDisposalAndTransparency() => Sta.Run(() =>
    {
        // 4x4 canvas. palette: 0 black, 1 red, 2 blue, 3 green
        var gif = GifWriter.Build(4, 4,
            new GifWriter.F(0, 0, 4, 4, GifWriter.Fill(16, 1), 0, 1),                    // f0: full red, keep, no delay → 100ms default
            new GifWriter.F(1, 1, 2, 2, GifWriter.Fill(4, 2), 5, 2),                     // f1: blue 2x2 at (1,1), 50ms, restore-to-background after
            new GifWriter.F(0, 0, 1, 1, GifWriter.Fill(1, 3), 1, 1));                    // f2: green px at (0,0), 1cs → clamped to 100ms
        var img = ImageDecoder.Decode(gif);
        Assert.Equal(4, img.Width); Assert.True(img.IsAnimated); Assert.Equal(3, img.Frames.Count);
        Assert.Equal(new[] { 100, 50, 100 }, img.Frames.Select(f => f.DelayMs));

        (byte b, byte g, byte r, byte a) P(int f, int x, int y) { var p = img.Frames[f].Pbgra; int i = (y * 4 + x) * 4; return (p[i], p[i + 1], p[i + 2], p[i + 3]); }
        Assert.Equal((0, 0, 255, 255), P(0, 3, 3));                     // red
        Assert.Equal((255, 0, 0, 255), P(1, 1, 1));                     // blue block
        Assert.Equal((0, 0, 255, 255), P(1, 0, 0));                     // red still around it
        Assert.Equal(0, P(2, 1, 1).a);                                  // disposal=2 cleared the block to transparent
        Assert.Equal((0, 255, 0, 255), P(2, 0, 0));                     // green dot drawn at offset
        Assert.Equal((0, 0, 255, 255), P(2, 3, 3));                     // untouched red
        Assert.Equal(0, img.FrameIndexAt(0)); Assert.Equal(1, img.FrameIndexAt(100)); Assert.Equal(2, img.FrameIndexAt(150)); Assert.Equal(0, img.FrameIndexAt(250));
    });

    [Fact]
    public void PngAndJpegStillsDecode() => Sta.Run(() =>
    {
        var px = new byte[8 * 8 * 4]; for (int i = 0; i < px.Length; i += 4) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 255; px[i + 3] = 255; }
        var bs = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, px, 32);
        foreach (BitmapEncoder enc in new BitmapEncoder[] { new PngBitmapEncoder(), new JpegBitmapEncoder(), new BmpBitmapEncoder() })
        {
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var ms = new MemoryStream(); enc.Save(ms);
            var img = ImageDecoder.Decode(ms.ToArray());
            Assert.False(img.IsAnimated); Assert.Equal(8, img.Width);
            Assert.InRange(img.Frames[0].Pbgra[2], 240, 255);
        }
    });

    [Fact]
    public void GarbageAndHugeHeadersAreRejectedBeforeAllocation() => Sta.Run(() =>
    {
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(Array.Empty<byte>()));
        // BMP header claiming 30000x30000 24-bit with no pixel data.
        var bmp = new byte[54];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M'; BitConverter.GetBytes(54).CopyTo(bmp, 10); BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(30000).CopyTo(bmp, 18); BitConverter.GetBytes(30000).CopyTo(bmp, 22); bmp[26] = 1; bmp[28] = 24;
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(bmp));
    });
}

public class CompositionRendererTests
{
    private static CompositionRenderer.AssetLoader None => _ => null;

    [Fact]
    public void BackgroundFillsCanvasAndOutputIsOpaque() => Sta.Run(() =>
    {
        var px = CompositionRenderer.RenderFrame(new DisplayComposition { Background = new(10, 200, 30) }, None, 0);
        Assert.Equal(TftAnimation.FrameBytesBgra, px.Length);
        Assert.Equal((10, 200, 30, 255), Px.At(px, 0, 0)); Assert.Equal((10, 200, 30, 255), Px.At(px, 127, 127));
    });

    [Fact]
    public void ImageLayerPlacementScaleAndOrder() => Sta.Run(() =>
    {
        // 2x1 image: left red, right blue.
        var src = new byte[2 * 1 * 4] { 0, 0, 255, 255, 255, 0, 0, 255 };
        var img = new DecodedImage(2, 1, new[] { new DecodedFrame(src, 2, 1, 0) });
        var c = new DisplayComposition();
        var layer = new ImageLayer { Asset = "x", Fit = ImageFit.Stretch };
        c.Layers.Add(layer);
        CompositionRenderer.AssetLoader load = l => img;
        var stretched = CompositionRenderer.RenderFrame(c, load, 0);
        Assert.True(Px.At(stretched, 10, 64).r > 200); Assert.True(Px.At(stretched, 117, 64).b > 200);

        layer.Scale = 0.5; layer.X = 32; // half size, centred 32px right of centre → covers x 48..112? width 64 centred at 96 → 64..128
        var moved = CompositionRenderer.RenderFrame(c, load, 0);
        Assert.Equal((0, 0, 0, 255), Px.At(moved, 20, 64));              // empty background left of the layer
        Assert.True(Px.At(moved, 70, 64).r > 200);
        Assert.True(Px.At(moved, 120, 64).b > 200);

        // A hidden layer on top does not draw; a visible one does.
        var overlay = new TextLayer { Text = "██", FontSize = 80, Color = new(0, 255, 0), Shadow = false, Visible = false };
        c.Layers.Add(overlay);
        Assert.Equal(Px.At(moved, 64, 64), Px.At(CompositionRenderer.RenderFrame(c, load, 0), 64, 64));
    });

    [Fact]
    public void TextLayerDrawsPixelsAndRespectsPosition() => Sta.Run(() =>
    {
        var c = new DisplayComposition();
        c.Layers.Add(new TextLayer { Text = "WWW", FontSize = 30, Color = new(255, 255, 255), Shadow = false, Y = -40 });
        var px = CompositionRenderer.RenderFrame(c, None, 0);
        int top = 0, bottom = 0;
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) if (Px.At(px, x, y).r > 128) { if (y < 64) top++; else bottom++; }
        Assert.True(top > 100, $"top={top}"); Assert.Equal(0, bottom);
        var b = CompositionRenderer.Bounds(c.Layers[0], None);
        Assert.InRange(b.Y + b.Height / 2, 23, 25);                         // centre ≈ 64 − 40
    });

    [Fact]
    public void BuildProducesAnimationWithPreservedDurationAndFrameCap() => Sta.Run(() =>
    {
        var frames = Enumerable.Range(0, 200).Select(i => { var p = new byte[4 * 4 * 4]; for (int k = 0; k < p.Length; k += 4) { p[k + 2] = (byte)i; p[k + 3] = 255; } return new DecodedFrame(p, 4, 4, 40); }).ToList();
        var img = new DecodedImage(4, 4, frames);
        var c = new DisplayComposition { MaxFrames = 50 };
        c.Layers.Add(new ImageLayer { Asset = "g", Fit = ImageFit.Stretch });
        var built = CompositionRenderer.Build(c, _ => img);
        Assert.Equal(50, built.Animation.Frames.Count);
        Assert.Equal(200 * 40, built.Animation.TotalDurationMs);
        Assert.Contains(built.Warnings, w => w.Contains("сокращено"));
        Assert.True(Px.At(built.Animation.Frames[25].Bgra, 5, 5).r > Px.At(built.Animation.Frames[1].Bgra, 5, 5).r);
    });

    [Fact]
    public void MissingAssetAndMultipleAnimatedLayersProduceWarningsNotCrashes() => Sta.Run(() =>
    {
        var anim = new DecodedImage(2, 2, Enumerable.Range(0, 3).Select(i => new DecodedFrame(new byte[16], 2, 2, 50)).ToList());
        var c = new DisplayComposition();
        c.Layers.Add(new ImageLayer { Name = "a", Asset = "a" }); c.Layers.Add(new ImageLayer { Name = "b", Asset = "b" }); c.Layers.Add(new ImageLayer { Name = "gone", Asset = "z" });
        var built = CompositionRenderer.Build(c, l => l.Asset == "z" ? null : anim);
        Assert.Equal(2, built.Warnings.Count);
        Assert.Equal(3, built.Animation.Frames.Count);
    });

    [Fact]
    public void ThumbnailPngIsValid() => Sta.Run(() =>
    {
        var png = CompositionRenderer.EncodePng(Px.Solid(1, 2, 3));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    });
}
