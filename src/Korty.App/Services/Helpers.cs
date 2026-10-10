using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Korty.Core.Protocol;

namespace Korty.App;

public static class Theme
{
    public static Brush Brush(string key)
    {
        try { if (Application.Current?.TryFindResource(key) is Brush b) return b; } catch { /* design-time / tests */ }
        return Brushes.Gray;
    }
    public static Brush Good => Brush("GoodBrush");
    public static Brush Warn => Brush("WarnBrush");
    public static Brush Bad => Brush("BadBrush");
    public static Brush Info => Brush("InfoBrush");
    public static Brush Muted => Brush("MutedBrush");
}

public static class PacketFormatter
{
    /// <summary>Human-readable dry-run listing. Shows the first and last packets in hex; never sends anything.</summary>
    public static string Describe(IReadOnlyList<ProtocolPacket> packets, int head = 3, int tail = 1, int bytesShown = 24)
    {
        var sb = new StringBuilder();
        long total = packets.Sum(p => (long)p.WireLength);
        sb.AppendLine($"DRY-RUN · ничего не отправлено · пакетов: {packets.Count} · байт на проводе: {total:N0}");
        void One(int i)
        {
            var p = packets[i];
            var buf = p.ToWireBuffer();
            var hex = string.Join(" ", buf.Take(bytesShown).Select(b => b.ToString("X2")));
            sb.AppendLine($"#{i + 1,4}  {p.CommandName,-24} {p.Kind,-7} →{p.Target,-7} len={p.WireLength,5}  риск={p.Risk}");
            sb.AppendLine($"        {hex}{(buf.Length > bytesShown ? " …" : "")}");
            if (p.Assumption != null) sb.AppendLine($"        допущение: {p.Assumption}");
        }
        int n = packets.Count;
        for (int i = 0; i < Math.Min(head, n); i++) One(i);
        if (n > head + tail) sb.AppendLine($"        … ещё {n - head - tail} пакет(ов) …");
        for (int i = Math.Max(head, n - tail); i < n; i++) One(i);
        return sb.ToString();
    }
}

public static class ImageUtil
{
    public static BitmapSource FromBgra(byte[] bgra, int size = 128)
    {
        var bs = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bgra, size * 4);
        bs.Freeze();
        return bs;
    }

    public static BitmapImage? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;  // do not hold the file open
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }
}

public sealed record Row(string A, string B = "", string C = "", string D = "");

public sealed record CapabilityRow(string Name, string Status, string Detail, Brush Brush);
