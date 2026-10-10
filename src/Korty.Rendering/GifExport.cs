using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Korty.Core.Tft;

namespace Korty.Rendering;

public static class GifExport
{
    /// <summary>Writes an animation as a GIF (default 100 ms frame delay on re-import, which matches the generators).</summary>
    public static void Save(TftAnimation anim, string path)
    {
        var enc = new GifBitmapEncoder();
        foreach (var f in anim.Frames)
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(128, 128, 96, 96, PixelFormats.Bgra32, null, f.Bgra, 128 * 4)));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
