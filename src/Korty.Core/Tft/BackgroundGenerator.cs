using Korty.Core.Model;

namespace Korty.Core.Tft;

public enum BackgroundKind { Aurora, Plasma, Matrix, Rings }

/// <summary>
/// Procedural animated backgrounds for the 128×128 screen: seamless loops (last frame flows into the first), deterministic,
/// pure maths — nothing is sent anywhere; the result is a normal <see cref="TftAnimation"/> the user can edit and upload.
/// </summary>
public static class BackgroundGenerator
{
    public const int DefaultFrames = 12;
    public const int FrameDelayMs = 100;

    public static string Title(BackgroundKind k) => k switch { BackgroundKind.Aurora => "Аврора", BackgroundKind.Plasma => "Плазма", BackgroundKind.Matrix => "Матрица", _ => "Неоновые кольца" };

    public static (RgbColor a, RgbColor b) DefaultColors(BackgroundKind k) => k switch
    {
        BackgroundKind.Aurora => (RgbColor.Parse("#3DDC97"), RgbColor.Parse("#6AA9FF")),
        BackgroundKind.Plasma => (RgbColor.Parse("#FF5A1F"), RgbColor.Parse("#B38CFF")),
        BackgroundKind.Matrix => (RgbColor.Parse("#3DDC97"), RgbColor.Parse("#000000")),
        _ => (RgbColor.Parse("#00E5FF"), RgbColor.Parse("#FF2BD6")),
    };

    public static TftAnimation Generate(BackgroundKind kind, RgbColor a, RgbColor b, int frames = DefaultFrames)
    {
        frames = Math.Clamp(frames, 2, 60);
        var list = new List<TftFrame>(frames);
        for (int f = 0; f < frames; f++)
            list.Add(new TftFrame(Frame(kind, a, b, 2 * Math.PI * f / frames, f, frames), FrameDelayMs));
        return new TftAnimation(list);
    }

    private static byte[] Frame(BackgroundKind kind, RgbColor a, RgbColor b, double ph, int f, int frames)
    {
        var px = new byte[TftAnimation.FrameBytesBgra];
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                (double r, double g, double bl) = kind switch
                {
                    BackgroundKind.Aurora => Aurora(x, y, ph, a, b),
                    BackgroundKind.Plasma => Plasma(x, y, ph, a, b),
                    BackgroundKind.Matrix => Matrix(x, y, f, frames, a, b),
                    _ => Rings(x, y, ph, a, b),
                };
                int o = (y * 128 + x) * 4;
                px[o] = (byte)Math.Clamp(bl, 0, 255); px[o + 1] = (byte)Math.Clamp(g, 0, 255); px[o + 2] = (byte)Math.Clamp(r, 0, 255); px[o + 3] = 255;
            }
        return px;
    }

    private static (double, double, double) Mix(RgbColor a, RgbColor b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static (double, double, double) Plasma(int x, int y, double ph, RgbColor a, RgbColor b)
    {
        double v = Math.Sin(x / 16.0 + ph) + Math.Sin(y / 9.0 - ph) + Math.Sin((x + y) / 16.0 + ph) + Math.Sin(Math.Sqrt((x - 64.0) * (x - 64.0) + (y - 64.0) * (y - 64.0)) / 8.0 - ph);
        return Mix(a, b, (v + 4) / 8);
    }

    private static (double, double, double) Aurora(int x, int y, double ph, RgbColor a, RgbColor b)
    {
        double band = 0.5 + 0.5 * Math.Sin(y / 11.0 + ph + 0.9 * Math.Sin(x / 21.0 + ph));
        double fade = Math.Pow(1 - y / 160.0, 1.4);                         // brighter at the top, dark at the bottom
        var (r, g, bl) = Mix(a, b, 0.5 + 0.5 * Math.Sin(x / 30.0 - ph));
        double k = band * fade * 1.15;
        return (r * k + 4, g * k + 6, bl * k + 14);
    }

    private static (double, double, double) Rings(int x, int y, double ph, RgbColor a, RgbColor b)
    {
        double d = Math.Sqrt((x - 63.5) * (x - 63.5) + (y - 63.5) * (y - 63.5));
        double v = 0.5 + 0.5 * Math.Sin(d / 5.5 - 2 * ph);
        var (r, g, bl) = Mix(a, b, 0.5 + 0.5 * Math.Sin(d / 40.0 - ph));
        double k = Math.Pow(v, 3) * 1.1 + 0.04;
        return (r * k, g * k, bl * k);
    }

    private static (double, double, double) Matrix(int x, int y, int f, int frames, RgbColor a, RgbColor bg)
    {
        int col = x / 5, cell = y / 5;
        double h = Hash(col);
        int speed = 1 + (int)(h * 2.99);                                          // 1..3 whole laps per loop → seamless
        double head = (h * 26 + f * 26.0 * speed / frames) % 26;                  // 26 cells tall
        double d = ((head - cell) % 26 + 26) % 26;
        double k = d < 12 ? Math.Pow(1 - d / 12, 1.6) : 0;
        if (x % 5 == 4 || y % 5 == 4) k *= 0.15;                                  // gaps between glyph cells
        if (d < 1) k = 1;
        return (bg.R + (a.R - bg.R) * k, bg.G + (a.G - bg.G) * k, bg.B + (a.B - bg.B) * k);
    }

    private static double Hash(int n) { var q = Math.Sin(n * 127.1 + 311.7) * 43758.5453; return q - Math.Floor(q); }
}
