using System.Text.Json.Serialization;

namespace Korty.Core.Model;

public enum ImageFit { Cover, Contain, Stretch }
public enum TextAlign { Left, Center, Right }

/// <summary>Layer coordinates: X/Y are the layer CENTRE offset in pixels from the centre of the 128×128 canvas.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ImageLayer), "image")]
[JsonDerivedType(typeof(TextLayer), "text")]
public abstract class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Opacity { get; set; } = 1.0;
}

public sealed class ImageLayer : Layer
{
    /// <summary>File name inside the design's assets folder (validated by <see cref="Storage.LibraryPaths"/>).</summary>
    public string Asset { get; set; } = "";
    public ImageFit Fit { get; set; } = ImageFit.Cover;
    /// <summary>Multiplier on top of <see cref="Fit"/>.</summary>
    public double Scale { get; set; } = 1.0;
    public double Rotation { get; set; }
}

public sealed class TextLayer : Layer
{
    public string Text { get; set; } = "KORTY";
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 18;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public RgbColor Color { get; set; } = new(255, 255, 255);
    public TextAlign Align { get; set; } = TextAlign.Center;
    /// <summary>1px black drop shadow for legibility on busy images.</summary>
    public bool Shadow { get; set; } = true;
    public double Rotation { get; set; }
}

/// <summary>
/// What the user designs. This is a LOCAL document: nothing in it implies the keyboard can show it. The firmware has no
/// documented API for live widgets, so clock/battery/etc. are NOT layer types — only static pixels are uploaded.
/// </summary>
public sealed class DisplayComposition
{
    public const int CurrentSchema = 1;
    public const int MaxLayers = 24;
    public const int MaxTextLength = 120;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новое оформление";
    public RgbColor Background { get; set; } = new(0, 0, 0);
    /// <summary>Bottom → top.</summary>
    public List<Layer> Layers { get; set; } = new();
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Output frame-rate cap for animated sources, applied by the renderer (0 = keep source timing).</summary>
    public int MaxFrames { get; set; } = 60;

    /// <summary>Repairs hand-edited / corrupt documents so the renderer can trust the numbers.</summary>
    public void Sanitize()
    {
        if (Layers.Count > MaxLayers) Layers.RemoveRange(MaxLayers, Layers.Count - MaxLayers);
        foreach (var l in Layers)
        {
            l.X = Finite(l.X); l.Y = Finite(l.Y);
            l.Opacity = Math.Clamp(Finite(l.Opacity, 1), 0, 1);
            switch (l)
            {
                case ImageLayer i:
                    i.Scale = Math.Clamp(Finite(i.Scale, 1), 0.05, 20);
                    i.Rotation = Finite(i.Rotation) % 360;
                    break;
                case TextLayer t:
                    t.FontSize = Math.Clamp(Finite(t.FontSize, 18), 6, 128);
                    if (t.Text.Length > MaxTextLength) t.Text = t.Text[..MaxTextLength];
                    t.Rotation = Finite(t.Rotation) % 360;
                    if (string.IsNullOrWhiteSpace(t.FontFamily)) t.FontFamily = "Segoe UI";
                    break;
            }
        }
        MaxFrames = Math.Clamp(MaxFrames, 1, 255);
    }

    private static double Finite(double v, double fallback = 0) => double.IsFinite(v) ? v : fallback;
}
