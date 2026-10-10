using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Korty.Core.Model;

namespace Korty.App.Controls;

/// <summary>
/// Schematic 75 % board with TFT panel and knob. Layout is generic (not an exact AK820 Pro key map). With
/// <see cref="Animate"/> it shows a rough local preview of the selected effect (see <see cref="RgbPreviewEngine"/>): one light
/// render loop, no HID traffic. Clicking a key (or typing, see <see cref="Press"/>) feeds reactive modes (Key on/off, Ripple…).
/// </summary>
public sealed class KeyboardView : FrameworkElement
{
    public static readonly DependencyProperty KeyColorProperty = Reg(nameof(KeyColor), Colors.OrangeRed);
    public static readonly DependencyProperty BrightnessProperty = Reg(nameof(Brightness), 3);
    public static readonly DependencyProperty ActiveProperty = Reg(nameof(Active), true);
    public static readonly DependencyProperty ScreenImageProperty = Reg<ImageSource?>(nameof(ScreenImage), null);
    public static readonly DependencyProperty ScreenBlackProperty = Reg(nameof(ScreenBlack), false);
    public static readonly DependencyProperty ScreenCaptionProperty = Reg(nameof(ScreenCaption), "TFT 128×128");
    public static readonly DependencyProperty ModeProperty = Reg(nameof(Mode), 1);
    public static readonly DependencyProperty SpeedProperty = Reg(nameof(Speed), 3);
    public static readonly DependencyProperty DirectionProperty = Reg(nameof(Direction), 0);
    public static readonly DependencyProperty RainbowProperty = Reg(nameof(Rainbow), false);
    public static readonly DependencyProperty AnimateProperty =
        DependencyProperty.Register(nameof(Animate), typeof(bool), typeof(KeyboardView), new FrameworkPropertyMetadata(false, (d, _) => ((KeyboardView)d).UpdateLoop()));

    private static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(KeyboardView), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color KeyColor { get => (Color)GetValue(KeyColorProperty); set => SetValue(KeyColorProperty, value); }
    public int Brightness { get => (int)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    public ImageSource? ScreenImage { get => (ImageSource?)GetValue(ScreenImageProperty); set => SetValue(ScreenImageProperty, value); }
    public bool ScreenBlack { get => (bool)GetValue(ScreenBlackProperty); set => SetValue(ScreenBlackProperty, value); }
    public string ScreenCaption { get => (string)GetValue(ScreenCaptionProperty); set => SetValue(ScreenCaptionProperty, value); }
    public int Mode { get => (int)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public int Speed { get => (int)GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }
    public int Direction { get => (int)GetValue(DirectionProperty); set => SetValue(DirectionProperty, value); }
    public bool Rainbow { get => (bool)GetValue(RainbowProperty); set => SetValue(RainbowProperty, value); }
    public bool Animate { get => (bool)GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }

    private const double BoardUnits = 21.6, BoardRows = 6.6, Inset = 0.15;

    private static readonly (string label, double w)[][] Rows =
    {
        K("Esc","F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12","Ins","Del","◉"),
        Row(("`",1),("1",1),("2",1),("3",1),("4",1),("5",1),("6",1),("7",1),("8",1),("9",1),("0",1),("-",1),("=",1),("⌫",2),("Hm",1)),
        Row(("Tab",1.5),("Q",1),("W",1),("E",1),("R",1),("T",1),("Y",1),("U",1),("I",1),("O",1),("P",1),("[",1),("]",1),("\\",1.5),("PU",1)),
        Row(("Caps",1.75),("A",1),("S",1),("D",1),("F",1),("G",1),("H",1),("J",1),("K",1),("L",1),(";",1),("'",1),("Enter",2.25),("PD",1)),
        Row(("Shift",2.25),("Z",1),("X",1),("C",1),("V",1),("B",1),("N",1),("M",1),(",",1),(".",1),("/",1),("Shift",1.75),("↑",1),("End",1)),
        Row(("Ctrl",1.25),("Win",1.25),("Alt",1.25),("Space",6.25),("Alt",1),("Fn",1),("Ctrl",1),("←",1),("↓",1),("→",1)),
    };
    private static (string, double)[] K(params string[] labels) => labels.Select(l => (l, 1.0)).ToArray();
    private static (string, double)[] Row(params (string, double)[] keys) => keys;

    // key rectangles in board units (computed once)
    private sealed record KeyBox(string Label, double X, double Y, double W);
    private static readonly List<KeyBox> Boxes = BuildBoxes();
    private static List<KeyBox> BuildBoxes()
    {
        var l = new List<KeyBox>();
        for (int r = 0; r < Rows.Length; r++) { double x = 0; foreach (var (label, w) in Rows[r]) { l.Add(new KeyBox(label, x, r, w)); x += w; } }
        return l;
    }

    private readonly List<RgbPreviewEngine.Press> _presses = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _looping;
    private long _lastFrame;

    public KeyboardView()
    {
        Loaded += (_, _) => UpdateLoop();
        Unloaded += (_, _) => StopLoop();
        IsVisibleChanged += (_, _) => UpdateLoop();
    }

    // ---- one light loop, only while visible, animating and loaded ----
    private void UpdateLoop() { if (Animate && IsLoaded && IsVisible) StartLoop(); else StopLoop(); }
    private void StartLoop() { if (_looping) return; _looping = true; CompositionTarget.Rendering += OnFrame; }
    private void StopLoop() { if (!_looping) return; _looping = false; CompositionTarget.Rendering -= OnFrame; }
    private void OnFrame(object? s, EventArgs e)
    {
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastFrame < 33) return;                 // ~30 fps is plenty
        _lastFrame = now; InvalidateVisual();
    }

    // ---- key reactions ----
    /// <summary>Feed a key press by label (letters/digits as printed on the schematic). Returns false for unknown labels.</summary>
    public bool Press(string label)
    {
        var box = Boxes.FirstOrDefault(b => string.Equals(b.Label, label, StringComparison.OrdinalIgnoreCase) && b.Label != "◉");
        if (box == null) return false;
        AddPress(box); return true;
    }

    private void AddPress(KeyBox b)
    {
        double t = _clock.Elapsed.TotalSeconds;
        _presses.RemoveAll(p => t - p.T > 3.5);
        _presses.Add(new RgbPreviewEngine.Press((b.X + b.W / 2) / 16.0, (b.Y + 0.5) / 6.0, t));
        if (!Animate) InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        double u = ActualWidth / BoardUnits; if (u <= 0) return;
        var p = e.GetPosition(this);
        double bx = p.X / u - Inset - 0.2, by = p.Y / u - Inset - 0.2;
        var hit = Boxes.FirstOrDefault(b => bx >= b.X && bx < b.X + b.W && by >= b.Y && by < b.Y + 1 && b.Label != "◉");
        if (hit != null) AddPress(hit);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        double h = w / BoardUnits * BoardRows;
        if (!double.IsInfinity(availableSize.Height) && h > availableSize.Height) { h = availableSize.Height; w = h / BoardRows * BoardUnits; }
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double u = ActualWidth / BoardUnits;
        if (u <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));     // hit-testable
        dc.PushTransform(new TranslateTransform(Inset * u, Inset * u));

        var baseColor = Active ? KeyColor : Color.FromRgb(0x4A, 0x4F, 0x5A);
        double t = _clock.Elapsed.TotalSeconds;
        var settings = new RgbSettings
        {
            Mode = Math.Clamp(Mode, 0, 19), Brightness = Math.Clamp(Brightness, 0, 5), Speed = Math.Clamp(Speed, 0, 5), Direction = Math.Clamp(Direction, 0, 3),
            ColorMode = Rainbow ? 1 : 0, Primary = new RgbColor(baseColor.R, baseColor.G, baseColor.B),
        };
        int b = settings.Brightness;
        double staticGlow = Active ? (b == 0 ? 0.0 : 0.22 + 0.15 * b) : 0.18;

        var chassis = new SolidColorBrush(Color.FromRgb(0x11, 0x12, 0x16));
        dc.DrawRoundedRectangle(chassis, new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x32)), 1), new Rect(0, 0, 16.4 * u, 6.4 * u), u * 0.5, u * 0.5);

        double gap = u * 0.08, pad = u * 0.2;
        var faceBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x23));
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var penW = Math.Max(1, u * 0.05);
        _presses.RemoveAll(p => t - p.T > 4);

        foreach (var kb in Boxes)
        {
            var rect = new Rect(pad + kb.X * u + gap, pad + kb.Y * u + gap, kb.W * u - 2 * gap, u - 2 * gap);
            if (kb.Label == "◉")
            {
                var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                dc.DrawEllipse(faceBrush, new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x5F, 0x6B)), 1.5), c, rect.Width * 0.42, rect.Height * 0.42);
                dc.DrawLine(new Pen(Brushes.Gray, 1.5), c, new Point(c.X, c.Y - rect.Height * 0.35));
                continue;
            }
            Color kc; double glow;
            if (Animate && Active)
            {
                var (r, g, bl) = RgbPreviewEngine.Color(settings, (kb.X + kb.W / 2) / 16.0, (kb.Y + 0.5) / 6.0, t, _presses);
                kc = Color.FromRgb(r, g, bl);
                double lum = Math.Max(r, Math.Max(g, bl)) / 255.0;
                glow = 0.12 + 0.88 * lum;
                if (lum > 0.02) { var m = Math.Max(r, Math.Max(g, bl)); kc = Color.FromRgb((byte)Math.Min(255, r * 255 / m), (byte)Math.Min(255, g * 255 / m), (byte)Math.Min(255, bl * 255 / m)); }
            }
            else { kc = baseColor; glow = staticGlow; }
            var tinted = new SolidColorBrush(Color.FromArgb((byte)(255 * glow * 0.3), kc.R, kc.G, kc.B));
            var edge = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, glow + 0.1)), kc.R, kc.G, kc.B)), penW);
            dc.DrawRoundedRectangle(faceBrush, null, rect, u * 0.14, u * 0.14);
            dc.DrawRoundedRectangle(tinted, edge, rect, u * 0.14, u * 0.14);
            if (kb.Label.Length > 0 && kb.Label != "Space")
            {
                var ft = new FormattedText(kb.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                    Math.Max(6, u * (kb.Label.Length > 2 ? 0.22 : 0.3)), new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAD)), ppd);
                dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
            }
        }

        // TFT panel
        double px = 16.9 * u, pw = 4.3 * u;
        dc.DrawRoundedRectangle(chassis, new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x32)), 1), new Rect(px, 0, pw, 5.2 * u), u * 0.5, u * 0.5);
        double s = pw - 1.0 * u;
        var screen = new Rect(px + 0.5 * u, 0.5 * u, s, s);
        dc.DrawRoundedRectangle(Brushes.Black, new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x37, 0x40)), 1), screen, u * 0.12, u * 0.12);
        if (ScreenImage != null && !ScreenBlack && Active)
        {
            dc.PushClip(new RectangleGeometry(screen, u * 0.12, u * 0.12));
            dc.DrawImage(ScreenImage, screen);
            dc.Pop();
        }
        else
        {
            var msg = !Active ? "нет\nподключения" : ScreenBlack ? "чёрный экран" : "";
            if (msg.Length > 0)
            {
                var ft = new FormattedText(msg, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Math.Max(8, u * 0.34),
                    new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x7C)), ppd) { TextAlignment = TextAlignment.Center, MaxTextWidth = s };
                dc.DrawText(ft, new Point(screen.X, screen.Y + (s - ft.Height) / 2));
            }
        }
        var cap = new FormattedText(ScreenCaption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Math.Max(8, u * 0.3),
            new SolidColorBrush(Color.FromRgb(0x8C, 0x92, 0xA1)), ppd) { MaxTextWidth = pw - u, TextAlignment = TextAlignment.Center };
        dc.DrawText(cap, new Point(px + 0.5 * u, screen.Bottom + 0.2 * u));
        dc.Pop();
    }
}
