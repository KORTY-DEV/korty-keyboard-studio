namespace Korty.Core.Tft;

/// <summary>RGB565, little-endian (low byte first), as documented by both open projects (32,768 bytes per 128×128 frame).</summary>
public static class Rgb565
{
    public const int FrameBytes = TftAnimation.Width * TftAnimation.Height * 2;

    public static ushort Pack(byte r, byte g, byte b) => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    public static byte[] FromBgra(ReadOnlySpan<byte> bgra, bool dither = false)
    {
        if (bgra.Length != TftAnimation.FrameBytesBgra) throw new ArgumentException("Expected 128x128 BGRA32.");
        return dither ? FloydSteinberg(bgra) : Plain(bgra);
    }

    private static byte[] Plain(ReadOnlySpan<byte> bgra)
    {
        var o = new byte[FrameBytes];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 2)
        {
            var (r, g, b) = Flatten(bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]);
            ushort c = Pack(r, g, b);
            o[j] = (byte)c; o[j + 1] = (byte)(c >> 8);
        }
        return o;
    }

    /// <summary>Alpha is composited over black: the physical screen has no transparency.</summary>
    private static (byte r, byte g, byte b) Flatten(byte r, byte g, byte b, byte a) =>
        a == 255 ? (r, g, b) : ((byte)(r * a / 255), (byte)(g * a / 255), (byte)(b * a / 255));

    private static byte[] FloydSteinberg(ReadOnlySpan<byte> bgra)
    {
        const int W = TftAnimation.Width, H = TftAnimation.Height;
        var err = new float[W * H * 3];
        var o = new byte[FrameBytes];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                var (r0, g0, b0) = Flatten(bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]);
                int e = (y * W + x) * 3;
                float r = Math.Clamp(r0 + err[e], 0, 255), g = Math.Clamp(g0 + err[e + 1], 0, 255), b = Math.Clamp(b0 + err[e + 2], 0, 255);
                int rq = (int)Math.Round(r / 255f * 31), gq = (int)Math.Round(g / 255f * 63), bq = (int)Math.Round(b / 255f * 31);
                float rr = Expand5(rq), gg = Expand6(gq), bb = Expand5(bq);
                float er = r - rr, eg = g - gg, eb = b - bb;
                Spread(err, x + 1, y, er, eg, eb, 7 / 16f);
                Spread(err, x - 1, y + 1, er, eg, eb, 3 / 16f);
                Spread(err, x, y + 1, er, eg, eb, 5 / 16f);
                Spread(err, x + 1, y + 1, er, eg, eb, 1 / 16f);
                ushort c = (ushort)((rq << 11) | (gq << 5) | bq);
                int j = (y * W + x) * 2;
                o[j] = (byte)c; o[j + 1] = (byte)(c >> 8);
            }
        return o;
    }

    private static void Spread(float[] err, int x, int y, float er, float eg, float eb, float k)
    {
        if (x < 0 || x >= TftAnimation.Width || y >= TftAnimation.Height) return;
        int e = (y * TftAnimation.Width + x) * 3;
        err[e] += er * k; err[e + 1] += eg * k; err[e + 2] += eb * k;
    }

    private static float Expand5(int v) => v * 255f / 31f;
    private static float Expand6(int v) => v * 255f / 63f;

    /// <summary>Inverse for simulation/verification (bit replication). Output BGRA32 opaque.</summary>
    public static byte[] ToBgra(ReadOnlySpan<byte> rgb565)
    {
        if (rgb565.Length != FrameBytes) throw new ArgumentException("Expected 32768 bytes.");
        var o = new byte[TftAnimation.FrameBytesBgra];
        for (int i = 0, j = 0; i < rgb565.Length; i += 2, j += 4)
        {
            int c = rgb565[i] | (rgb565[i + 1] << 8);
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            o[j] = (byte)((b << 3) | (b >> 2));
            o[j + 1] = (byte)((g << 2) | (g >> 4));
            o[j + 2] = (byte)((r << 3) | (r >> 2));
            o[j + 3] = 255;
        }
        return o;
    }
}
