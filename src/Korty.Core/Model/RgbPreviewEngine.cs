namespace Korty.Core.Model;

/// <summary>
/// One lightweight, honest APPROXIMATION of the 20 lighting effects for the on-screen preview. Pure function of
/// (settings, key position, time, key presses): no HID, no state. Names/modes come from the reference configurator; the real
/// firmware animations are not known precisely, so this is only a rough visual hint. Brightness, speed (0.35 + 0.38·speed as in the
/// reference preview) and direction are honoured.
/// </summary>
public static class RgbPreviewEngine
{
    public readonly record struct Press(double X, double Y, double T);

    public static double SpeedFactor(int speed) => 0.35 + Math.Clamp(speed, 0, 5) * 0.38;

    public static (byte r, byte g, byte b) Color(RgbSettings s, double x, double y, double t, IReadOnlyList<Press> presses)
    {
        if (s.Mode == 0 || s.Brightness == 0) return (0, 0, 0);
        double tt = t * SpeedFactor(s.Speed);
        double along = s.Direction switch { 0 => x, 3 => 1 - x, 2 => y, _ => 1 - y };
        double cx = x - 0.5, cy = (y - 0.5) * 0.45;                       // board is wide: squash y
        double dc = Math.Sqrt(cx * cx + cy * cy * 4);
        bool hue = s.ColorMode > 0 || s.Mode is 6 or 8;
        double h = 0, v = 1;

        switch (s.Mode)
        {
            case 1: break;                                                              // Static
            case 2: v = 0.06 + 0.94 * Pressed(presses, x, y, t); break;                 // Key on
            case 3: v = 1 - 0.94 * Pressed(presses, x, y, t); break;                    // Key off
            case 4: v = Hash(Math.Floor(x * 40) + Math.Floor(tt * 5), Math.Floor(y * 9) + 3) > 0.9 ? 1 : 0.12; h = Hash(x * 9, y * 7) * 360; break; // Glitter
            case 5: v = Math.Max(0.08, 1 - Frac(tt * 0.7 - y + Hash(Math.Floor(x * 16), 1)) * 3.2); break;                                       // Falling
            case 6: h = (x * 0.9 + y * 0.3) * 360 + tt * 20; break;                     // Colourful
            case 7: v = 0.5 + 0.5 * Math.Sin(tt * 2 * Math.PI * 0.6); break;            // Breathe
            case 8: h = tt * 60; break;                                                 // Spectrum
            case 9: h = tt * 80 - dc * 360; v = 0.55 + 0.45 * Math.Cos(2 * Math.PI * (dc * 1.5 - tt * 0.4)); break;                             // Outward
            case 10: v = Band(along, Frac(tt * 0.5), 0.35); break;                      // Scroll
            case 11: v = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (along * 2 - tt * 0.6)); break;                                                     // Roll
            case 12: { double a = Math.Atan2(cy * 2, cx) / (2 * Math.PI) + 0.5; v = Band(a, Frac(tt * 0.4), 0.3); } break;                       // Rotate
            case 13: v = 0.1 + 0.9 * Ripple(presses, x, y, t, 1.4, 0.16); h = 20; break;                                                        // Explode
            case 14: v = 0.1 + 0.9 * Launch(presses, x, y, t); break;                                                                            // Launch
            case 15: v = 0.1 + 0.9 * Ripple(presses, x, y, t, 0.9, 0.08); break;       // Ripple
            case 16: h = along * 360 - tt * 90; v = 0.6 + 0.4 * Math.Sin(2 * Math.PI * (along - tt * 0.3)); break;                               // Flow
            case 17: v = 0.5 + 0.5 * Math.Sin(tt * 4 - dc * 8); break;                  // Pulse
            case 18: v = Band((x + y) / 2, Frac(tt * 0.45), 0.3); break;                // Tilt
            case 19: { double head = Math.Abs(Frac(tt * 0.4) * 2 - 1); v = Band(along, head, 0.28); } break;                                    // Shuttle
        }

        double k = Math.Clamp(v, 0, 1) * (s.Brightness / 5.0);
        var (r, g, b) = hue ? Hsv(h, 1, 1) : (s.Primary.R, s.Primary.G, s.Primary.B);
        return ((byte)(r * k), (byte)(g * k), (byte)(b * k));
    }

    private static double Frac(double v) { v -= Math.Floor(v); return v; }
    private static double Hash(double a, double b) { var q = Math.Sin(a * 127.1 + b * 311.7) * 43758.5453; return q - Math.Floor(q); }
    private static double Band(double pos, double head, double width) => Math.Max(0, 1 - Math.Abs(((pos - head + 1.5) % 1) - 0.5) * 2 / width);

    private static double Pressed(IReadOnlyList<Press> presses, double x, double y, double t)
    {
        double best = 0;
        foreach (var p in presses)
            if (Math.Abs(p.X - x) < 0.03 && Math.Abs(p.Y - y) < 0.08) best = Math.Max(best, Math.Exp(-(t - p.T) * 2.2));
        return best;
    }

    private static double Ripple(IReadOnlyList<Press> presses, double x, double y, double t, double speed, double width)
    {
        double best = 0;
        foreach (var p in presses)
        {
            double age = t - p.T; if (age < 0 || age > 3) continue;
            double d = Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow((y - p.Y) * 0.5, 2));
            best = Math.Max(best, Math.Exp(-Math.Pow((d - age * speed * 0.5) / width, 2)) * Math.Exp(-age * 0.9));
        }
        return best;
    }

    private static double Launch(IReadOnlyList<Press> presses, double x, double y, double t)
    {
        double best = 0;
        foreach (var p in presses)
        {
            double age = t - p.T; if (age < 0 || age > 2) continue;
            if (Math.Abs(p.X - x) > 0.035) continue;
            double head = p.Y - age * 1.2;                                   // flies upwards
            best = Math.Max(best, Math.Exp(-Math.Pow((y - head) / 0.15, 2)) * Math.Exp(-age));
        }
        return best;
    }

    public static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360; double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double h, double s, double v) ToHsv(byte r, byte g, byte b, double keepHue = 0)
    {
        double R = r / 255.0, G = g / 255.0, B = b / 255.0, max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
        double h = d == 0 ? keepHue : max == R ? 60 * (((G - B) / d % 6 + 6) % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }
}
