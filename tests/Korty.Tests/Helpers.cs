using System.Runtime.ExceptionServices;
using Korty.Core.Hid;
using Korty.Core.Safety;
using Korty.Core.Tft;

namespace Korty.Tests;

public static class Sta
{
    public static T Run<T>(Func<T> f)
    {
        T result = default!; Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
    public static void Run(Action a) => Run<int>(() => { a(); return 0; });
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "korty-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public static class Px
{
    public static byte[] Solid(byte r, byte g, byte b)
    {
        var a = new byte[TftAnimation.FrameBytesBgra];
        for (int i = 0; i < a.Length; i += 4) { a[i] = b; a[i + 1] = g; a[i + 2] = r; a[i + 3] = 255; }
        return a;
    }
    public static (byte r, byte g, byte b, byte a) At(byte[] bgra, int x, int y, int width = 128)
    {
        int i = (y * width + x) * 4; return (bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]);
    }
    public static TftAnimation Anim(params (byte r, byte g, byte b, int ms)[] frames) =>
        new(frames.Select(f => new TftFrame(Solid(f.r, f.g, f.b), f.ms)).ToList());
}

public sealed class FakeEnumerator : IHidEnumerator
{
    public List<HidInterfaceInfo> Items { get; set; } = new();
    public Exception? Throw { get; set; }
    public IReadOnlyList<HidInterfaceInfo> Enumerate() => Throw != null ? throw Throw : Items.ToList();
}

public sealed class RecordingTransport : IHidTransport
{
    public List<ApprovedWrite> Writes { get; } = new();
    public int FailOnIndex { get; set; } = -1;
    public Task<TransportResult> SendAsync(ApprovedWrite write, CancellationToken ct)
    {
        if (FailOnIndex == Writes.Count) return Task.FromResult(TransportResult.Fail("boom"));
        Writes.Add(write);
        return Task.FromResult(new TransportResult(true, write.Packet.WireLength));
    }
}

public static class Hw
{
    public static HidInterfaceInfo Iface(ushort vid = 0x0C45, ushort pid = 0x8009, ushort page = 0xFF68, int outLen = 65, int featLen = 0,
        string path = @"\?\hid#vid_0c45&pid_8009&mi_01#x", string container = "c1", int mi = 1, int? col = 5, ushort usage = 1, bool read = true) => new()
    {
        Path = path, VendorId = vid, ProductId = pid, UsagePage = page, Usage = usage, OutputReportLength = outLen, FeatureReportLength = featLen,
        InputReportLength = 65, ContainerId = container, InterfaceNumber = mi, CollectionNumber = col, DescriptorRead = read,
    };
}

/// <summary>Minimal GIF89a writer (4-colour palette, clear-code-every-2-pixels LZW) so decoder tests need no binary fixtures.</summary>
public static class GifWriter
{
    public sealed record F(int Left, int Top, int W, int H, byte[] Indices, int DelayCs, int Disposal, int Transparent = -1);

    // palette: 0 black, 1 red, 2 blue, 3 green
    public static byte[] Build(int w, int h, params F[] frames)
    {
        using var ms = new MemoryStream();
        void B(params int[] b) { foreach (var x in b) ms.WriteByte((byte)x); }
        void W16(int v) { B(v & 0xFF, v >> 8); }
        ms.Write("GIF89a"u8);
        W16(w); W16(h); B(0xF1, 0, 0);
        B(0,0,0,  255,0,0,  0,0,255,  0,255,0);
        foreach (var f in frames)
        {
            B(0x21, 0xF9, 4, (f.Disposal << 2) | (f.Transparent >= 0 ? 1 : 0)); W16(f.DelayCs); B(f.Transparent >= 0 ? f.Transparent : 0, 0);
            B(0x2C); W16(f.Left); W16(f.Top); W16(f.W); W16(f.H); B(0);
            B(2); // LZW min code size
            var bits = new List<bool>();
            void Code(int c) { for (int i = 0; i < 3; i++) bits.Add(((c >> i) & 1) != 0); }
            for (int i = 0; i < f.Indices.Length; i++)
            {
                if (i % 2 == 0) Code(4);       // clear
                Code(f.Indices[i]);
            }
            Code(5); // EOI
            var bytes = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int v = 0; for (int k = 0; k < 8 && i + k < bits.Count; k++) if (bits[i + k]) v |= 1 << k;
                bytes.Add((byte)v);
            }
            for (int i = 0; i < bytes.Count; i += 255)
            {
                int n = Math.Min(255, bytes.Count - i);
                B(n); ms.Write(bytes.ToArray(), i, n);
            }
            B(0);
        }
        B(0x3B);
        return ms.ToArray();
    }

    public static byte[] Fill(int n, byte idx) { var a = new byte[n]; Array.Fill(a, idx); return a; }
}
