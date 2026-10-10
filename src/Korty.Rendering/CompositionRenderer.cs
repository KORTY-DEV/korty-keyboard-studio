using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Korty.Core.Model;
using Korty.Core.Tft;

namespace Korty.Rendering;

public sealed record BuiltDesign(TftAnimation Animation, IReadOnlyList<string> Warnings);

/// <summary>
/// Renders a <see cref="DisplayComposition"/> into 128×128 BGRA frames. Must be called on an STA thread
/// (the WPF UI thread, or a dedicated STA thread in tests/CLI).
/// </summary>
public static class CompositionRenderer
{
    public const int Size = TftAnimation.Width;
    private const int MaxTimelineFrames = 600;

    public delegate DecodedImage? AssetLoader(ImageLayer layer);

    /// <summary>The layer driving the timeline: the first visible image layer whose asset is animated. Other animated assets show frame 0.</summary>
    public static ImageLayer? FindAnimatedLayer(DisplayComposition c, AssetLoader assets) =>
        c.Layers.OfType<ImageLayer>().FirstOrDefault(l => l.Visible && assets(l) is { IsAnimated: true });

    /// <summary>One frame at <paramref name="timeMs"/> into the loop. Cheap enough for interactive editing.</summary>
    public static byte[] RenderFrame(DisplayComposition c, AssetLoader assets, double timeMs)
    {
        var animated = FindAnimatedLayer(c, assets);
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(dv, EdgeMode.Unspecified);
        TextOptions.SetTextRenderingMode(dv, TextRenderingMode.Grayscale);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(c.Background.R, c.Background.G, c.Background.B)), null, new Rect(0, 0, Size, Size));
            foreach (var layer in c.Layers)
            {
                if (!layer.Visible || layer.Opacity <= 0) continue;
                dc.PushOpacity(layer.Opacity);
                try
                {
                    switch (layer)
                    {
                        case ImageLayer il: DrawImage(dc, il, assets(il), ReferenceEquals(il, animated) ? timeMs : 0); break;
                        case TextLayer tl: DrawText(dc, tl); break;
                    }
                }
                finally { dc.Pop(); }
            }
        }
        var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[Size * Size * 4];
        rtb.CopyPixels(px, Size * 4, 0);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255; // screen has no alpha; background is opaque anyway
        return px;
    }

    private static void DrawImage(DrawingContext dc, ImageLayer l, DecodedImage? img, double timeMs)
    {
        if (img == null) return;
        var frame = img.Frames[img.FrameIndexAt(timeMs)].Source;
        double w = img.Width, h = img.Height;
        double sx, sy;
        switch (l.Fit)
        {
            case ImageFit.Stretch: sx = Size / w; sy = Size / h; break;
            case ImageFit.Contain: sx = sy = Math.Min(Size / w, Size / h); break;
            default: sx = sy = Math.Max(Size / w, Size / h); break;
        }
        sx *= l.Scale; sy *= l.Scale;
        dc.PushTransform(new TranslateTransform(Size / 2.0 + l.X, Size / 2.0 + l.Y));
        dc.PushTransform(new RotateTransform(l.Rotation));
        dc.PushTransform(new ScaleTransform(sx, sy));
        dc.DrawImage(frame, new Rect(-w / 2, -h / 2, w, h));
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    /// <summary>{time} → HH:mm, {date} → dd.MM at render time. A snapshot: the keyboard cannot update it by itself.</summary>
    public static string ExpandTokens(string text) =>
        text.Replace("{time}", DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)).Replace("{date}", DateTime.Now.ToString("dd.MM", CultureInfo.InvariantCulture));

    private static void DrawText(DrawingContext dc, TextLayer l)
    {
        if (string.IsNullOrEmpty(l.Text)) return;
        var text = ExpandTokens(l.Text);
        var typeface = new Typeface(new FontFamily(l.FontFamily), l.Italic ? FontStyles.Italic : FontStyles.Normal,
            l.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        FormattedText Make(Color color)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, l.FontSize, new SolidColorBrush(color), 1.0);
            return ft;
        }
        var probe = Make(Colors.White);
        double w = Math.Max(1, probe.Width), h = Math.Max(1, probe.Height);
        FormattedText Aligned(Color c)
        {
            var ft = Make(c);
            ft.MaxTextWidth = w + 1;
            ft.TextAlignment = l.Align switch { TextAlign.Left => TextAlignment.Left, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Center };
            return ft;
        }
        dc.PushTransform(new TranslateTransform(Size / 2.0 + l.X, Size / 2.0 + l.Y));
        dc.PushTransform(new RotateTransform(l.Rotation));
        if (l.Shadow) dc.DrawText(Aligned(Colors.Black), new Point(-w / 2 + 1, -h / 2 + 1));
        dc.DrawText(Aligned(Color.FromRgb(l.Color.R, l.Color.G, l.Color.B)), new Point(-w / 2, -h / 2));
        dc.Pop(); dc.Pop();
    }

    /// <summary>Hit-test box for the editor: layer bounds in canvas pixels (centre-based, ignoring rotation).</summary>
    public static Rect Bounds(Layer layer, AssetLoader assets)
    {
        double cx = Size / 2.0 + layer.X, cy = Size / 2.0 + layer.Y;
        switch (layer)
        {
            case ImageLayer il when assets(il) is { } img:
            {
                double w = img.Width, h = img.Height;
                double sx, sy;
                switch (il.Fit)
                {
                    case ImageFit.Stretch: sx = Size / w; sy = Size / h; break;
                    case ImageFit.Contain: sx = sy = Math.Min(Size / w, Size / h); break;
                    default: sx = sy = Math.Max(Size / w, Size / h); break;
                }
                double bw = w * sx * il.Scale, bh = h * sy * il.Scale;
                return new Rect(cx - bw / 2, cy - bh / 2, bw, bh);
            }
            case TextLayer tl:
            {
                var typeface = new Typeface(new FontFamily(tl.FontFamily), tl.Italic ? FontStyles.Italic : FontStyles.Normal, tl.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(tl.Text.Length == 0 ? " " : tl.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, tl.FontSize, Brushes.White, 1.0);
                return new Rect(cx - ft.Width / 2, cy - ft.Height / 2, ft.Width, ft.Height);
            }
        }
        return Rect.Empty;
    }

    // ---------------- full animation ----------------

    public static BuiltDesign Build(DisplayComposition c, AssetLoader assets)
    {
        var warnings = new List<string>();
        foreach (var l in c.Layers.OfType<ImageLayer>().Where(l => l.Visible))
            if (assets(l) == null) warnings.Add($"Ресурс слоя «{l.Name}» не найден или не читается — слой пропущен.");
        if (c.Layers.OfType<TextLayer>().Any(t => t.Visible && (t.Text.Contains("{time}") || t.Text.Contains("{date}"))))
            warnings.Add("Слой со временем/датой — снимок на момент загрузки: изображение статично, часы не идут. Клавиатура сама ничего не накладывает поверх своей GIF.");
        var animatedLayers = c.Layers.OfType<ImageLayer>().Where(l => l.Visible && assets(l) is { IsAnimated: true }).ToList();
        if (animatedLayers.Count > 1)
            warnings.Add($"Анимированных слоёв {animatedLayers.Count}: временную шкалу задаёт «{animatedLayers[0].Name}», остальные показаны первым кадром.");

        var driver = animatedLayers.FirstOrDefault();
        var frames = new List<TftFrame>();
        if (driver == null)
        {
            frames.Add(new TftFrame(RenderFrame(c, assets, 0), 0));
        }
        else
        {
            var img = assets(driver)!;
            var idx = Enumerable.Range(0, img.Frames.Count).ToList();
            if (idx.Count > MaxTimelineFrames)
            {
                double step = (double)idx.Count / MaxTimelineFrames;
                idx = Enumerable.Range(0, MaxTimelineFrames).Select(i => (int)(i * step)).ToList();
            }
            var startOf = new double[img.Frames.Count + 1];
            for (int s = 0; s < img.Frames.Count; s++) startOf[s + 1] = startOf[s] + img.Frames[s].DelayMs;
            // Each kept frame represents the span until the next kept frame, so total duration is preserved.
            for (int k = 0; k < idx.Count; k++)
            {
                int from = idx[k];
                int to = k + 1 < idx.Count ? idx[k + 1] : img.Frames.Count;
                int delay = (int)(startOf[to] - startOf[from]);
                frames.Add(new TftFrame(RenderFrame(c, assets, startOf[from] + 0.001), delay));
            }
        }
        var anim = new TftAnimation(frames);
        if (anim.Frames.Count > c.MaxFrames)
        {
            int before = anim.Frames.Count;
            anim = anim.Decimate(c.MaxFrames);
            warnings.Add($"Кадров сокращено с {before} до {anim.Frames.Count} (лимит {c.MaxFrames}); общая длительность сохранена.");
        }
        return new BuiltDesign(anim, warnings);
    }

    public static byte[] EncodePng(byte[] bgra, int size = Size)
    {
        var bs = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bgra, size * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource ToBitmapSource(byte[] bgra, int size = Size)
    {
        var bs = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bgra, size * 4);
        bs.Freeze();
        return bs;
    }
}
