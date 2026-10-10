using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Korty.Rendering;

public sealed class DecodedFrame
{
    public byte[] Pbgra { get; }
    public int DelayMs { get; }
    private BitmapSource? _source;
    private readonly int _w, _h;

    public DecodedFrame(byte[] pbgra, int w, int h, int delayMs) { Pbgra = pbgra; _w = w; _h = h; DelayMs = delayMs; }

    /// <summary>Frozen, therefore usable from any thread. Created lazily.</summary>
    public BitmapSource Source
    {
        get
        {
            if (_source != null) return _source;
            var bs = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Pbgra32, null, Pbgra, _w * 4);
            bs.Freeze();
            return _source = bs;
        }
    }
}

/// <summary>A decoded image: one frame for stills, N composed full-canvas frames for GIF.</summary>
public sealed class DecodedImage
{
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<DecodedFrame> Frames { get; }
    public bool IsAnimated => Frames.Count > 1;
    public int TotalDurationMs { get; }

    public DecodedImage(int w, int h, IReadOnlyList<DecodedFrame> frames)
    {
        Width = w; Height = h; Frames = frames;
        TotalDurationMs = frames.Sum(f => f.DelayMs);
    }

    /// <summary>Frame index shown at <paramref name="timeMs"/> for a looping animation.</summary>
    public int FrameIndexAt(double timeMs)
    {
        if (!IsAnimated || TotalDurationMs <= 0) return 0;
        double t = timeMs % TotalDurationMs;
        if (t < 0) t += TotalDurationMs;
        double acc = 0;
        for (int i = 0; i < Frames.Count; i++)
        {
            acc += Frames[i].DelayMs;
            if (t < acc) return i;
        }
        return Frames.Count - 1;
    }
}

/// <summary>
/// Local, offline decoding with explicit resource limits (decompression-bomb guard). GIF frames are composed on a
/// logical-screen canvas honouring frame offsets, transparency and disposal methods, so partial-frame GIFs
/// (the majority of real-world files) look right.
/// </summary>
public static class ImageDecoder
{
    public const long MaxFileBytes = 25L * 1024 * 1024;
    public const long MaxPixels = 16L * 1024 * 1024;       // per frame
    public const long MaxDecodedBytes = 400L * 1024 * 1024; // all frames
    public const int MaxSourceFrames = 2000;
    public const int MinDelayMs = 20;                       // browsers treat <=10 ms as 100 ms
    public const int DefaultDelayMs = 100;

    public static DecodedImage Decode(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Файл не найден.", path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("Файл больше 25 МБ.");
        return Decode(File.ReadAllBytes(path));
    }

    public static DecodedImage Decode(byte[] data)
    {
        // The stream stays open for the whole decode: with CacheOption.None WPF reads only the header up front,
        // so dimensions can be validated BEFORE any pixel buffer is allocated (decompression-bomb guard).
        using var ms = new MemoryStream(data, writable: false);
        BitmapDecoder decoder;
        try
        {
            decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("В файле нет кадров.");
            var first = decoder.Frames[0];
            CheckSize(first.PixelWidth, first.PixelHeight, 1);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException("Файл не распознан как PNG/JPEG/GIF/BMP: " + ex.Message, ex);
        }
        try
        {
            return decoder is GifBitmapDecoder gif ? DecodeGif(gif) : DecodeStill(decoder.Frames[0]);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException("Не удалось декодировать изображение: " + ex.Message, ex);
        }
    }

    private static void CheckSize(long w, long h, long frames)
    {
        if (w <= 0 || h <= 0) throw new InvalidDataException("Нулевой размер изображения.");
        if (w * h > MaxPixels) throw new InvalidDataException($"Изображение слишком большое ({w}×{h}); максимум {MaxPixels / 1_000_000} Мпикс.");
        if (w * h * 4 * frames > MaxDecodedBytes) throw new InvalidDataException("Анимация не поместится в память — уменьшите размер или число кадров.");
    }

    private static DecodedImage DecodeStill(BitmapFrame frame)
    {
        CheckSize(frame.PixelWidth, frame.PixelHeight, 1);
        BitmapSource src = ApplyExifOrientation(frame);
        var conv = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        return new DecodedImage(w, h, new[] { new DecodedFrame(px, w, h, 0) });
    }

    private static BitmapSource ApplyExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.GetQuery("/app1/ifd/{ushort=274}") is ushort o)
            {
                double angle = o switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
                if (angle != 0) { var t = new TransformedBitmap(frame, new RotateTransform(angle)); t.Freeze(); return t; }
            }
        }
        catch { /* metadata is optional */ }
        return frame;
    }

    private static DecodedImage DecodeGif(GifBitmapDecoder gif)
    {
        int count = gif.Frames.Count;
        if (count > MaxSourceFrames) throw new InvalidDataException($"В GIF {count} кадров (максимум {MaxSourceFrames}).");
        int W = 0, H = 0;
        if (gif.Metadata is BitmapMetadata gm)
        {
            W = Query(gm, "/logscrdesc/Width");
            H = Query(gm, "/logscrdesc/Height");
        }
        if (W <= 0 || H <= 0) { W = gif.Frames[0].PixelWidth; H = gif.Frames[0].PixelHeight; }
        CheckSize(W, H, count);

        var canvas = new byte[W * H * 4];
        var frames = new List<DecodedFrame>(count);
        byte[]? restore = null;
        (int l, int t, int w, int h, int disposal) prev = (0, 0, 0, 0, 0);

        for (int i = 0; i < count; i++)
        {
            // 1. dispose previous frame
            if (i > 0)
            {
                if (prev.disposal == 2) ClearRect(canvas, W, H, prev.l, prev.t, prev.w, prev.h);
                else if (prev.disposal == 3 && restore != null) Array.Copy(restore, canvas, canvas.Length);
            }
            var fr = gif.Frames[i];
            var md = fr.Metadata as BitmapMetadata;
            int left = md != null ? Query(md, "/imgdesc/Left") : 0;
            int top = md != null ? Query(md, "/imgdesc/Top") : 0;
            int disposal = md != null ? Query(md, "/grctlext/Disposal") : 0;
            int delayCs = md != null ? Query(md, "/grctlext/Delay") : 0;
            int delayMs = delayCs * 10;
            if (delayMs < MinDelayMs) delayMs = DefaultDelayMs;

            if (disposal == 3) restore = (byte[])canvas.Clone();

            var conv = new FormatConvertedBitmap(fr, PixelFormats.Pbgra32, null, 0);
            int fw = conv.PixelWidth, fh = conv.PixelHeight;
            var fpx = new byte[fw * fh * 4];
            conv.CopyPixels(fpx, fw * 4, 0);
            Blend(canvas, W, H, fpx, fw, fh, left, top);

            frames.Add(new DecodedFrame((byte[])canvas.Clone(), W, H, delayMs));
            prev = (left, top, fw, fh, disposal);
        }
        return new DecodedImage(W, H, frames);
    }

    private static int Query(BitmapMetadata md, string q)
    {
        try { return md.ContainsQuery(q) ? Convert.ToInt32(md.GetQuery(q)) : 0; }
        catch { return 0; }
    }

    private static void ClearRect(byte[] canvas, int W, int H, int l, int t, int w, int h)
    {
        for (int y = Math.Max(0, t); y < Math.Min(H, t + h); y++)
            for (int x = Math.Max(0, l); x < Math.Min(W, l + w); x++)
                Array.Clear(canvas, (y * W + x) * 4, 4);
    }

    /// <summary>Premultiplied source-over.</summary>
    internal static void Blend(byte[] canvas, int W, int H, byte[] src, int sw, int sh, int left, int top)
    {
        for (int y = 0; y < sh; y++)
        {
            int cy = y + top; if (cy < 0 || cy >= H) continue;
            for (int x = 0; x < sw; x++)
            {
                int cx = x + left; if (cx < 0 || cx >= W) continue;
                int si = (y * sw + x) * 4, ci = (cy * W + cx) * 4;
                int sa = src[si + 3];
                if (sa == 0) continue;
                if (sa == 255) { canvas[ci] = src[si]; canvas[ci + 1] = src[si + 1]; canvas[ci + 2] = src[si + 2]; canvas[ci + 3] = 255; continue; }
                int inv = 255 - sa;
                canvas[ci] = (byte)(src[si] + canvas[ci] * inv / 255);
                canvas[ci + 1] = (byte)(src[si + 1] + canvas[ci + 1] * inv / 255);
                canvas[ci + 2] = (byte)(src[si + 2] + canvas[ci + 2] * inv / 255);
                canvas[ci + 3] = (byte)(sa + canvas[ci + 3] * inv / 255);
            }
        }
    }
}
