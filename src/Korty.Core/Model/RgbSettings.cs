using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Korty.Core.Model;

[JsonConverter(typeof(RgbColorJsonConverter))]
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static RgbColor Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"Invalid colour '{text}'.");
}

public sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RgbColor.TryParse(reader.GetString(), out var c) ? c : default;

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToHex());
}

/// <summary>Effect names as listed by the reference web configurator (app.js LIGHT_MODES); direction only matters for four of them.</summary>
public static class RgbModes
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "Выкл", "Статичный", "Клавиша вкл", "Клавиша выкл", "Мерцание", "Падение", "Разноцветный", "Дыхание", "Спектр", "От центра",
        "Прокрутка", "Волна", "Вращение", "Взрыв", "Запуск", "Рябь", "Поток", "Пульс", "Наклон", "Челнок",
    };
    public static readonly IReadOnlyList<string> NamesEn = new[]
    {
        "Off", "Static", "Key on", "Key off", "Glitter", "Falling", "Colourful", "Breathe", "Spectrum", "Outward",
        "Scroll", "Roll", "Rotate", "Explode", "Launch", "Ripple", "Flow", "Pulse", "Tilt", "Shuttle",
    };
    /// <summary>Scroll (0x0A), Roll (0x0B), Flow (0x10), Tilt (0x12) — per the reference page text.</summary>
    public static bool UsesDirection(int mode) => mode is 0x0A or 0x0B or 0x10 or 0x12;
    public static string Name(int mode) => mode >= 0 && mode < Names.Count ? Names[mode] : "?";
}

/// <summary>
/// Global lighting settings. Ranges follow the two open protocol descriptions (mode 0..19, brightness 0..5, speed 0..5,
/// direction 0..3). Mode names come from the reference configurator (see <see cref="RgbModes"/>).
/// </summary>
public sealed record RgbSettings
{
    public const int ModeCount = 20;
    public int Mode { get; init; } = 1;
    public RgbColor Primary { get; init; } = new(0xFF, 0x45, 0x00);
    public RgbColor Secondary { get; init; } = new(0, 0, 0);
    /// <summary>0 = single colour, &gt;0 = rainbow/variants (semantics documented by one source only).</summary>
    public int ColorMode { get; init; }
    public int Brightness { get; init; } = 3;
    public int Speed { get; init; } = 3;
    /// <summary>0=left,1=down,2=up,3=right (applies to directional effects only).</summary>
    public int Direction { get; init; }

    public void Validate()
    {
        if (Mode is < 0 or >= ModeCount) throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Mode must be 0..19");
        if (Brightness is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(Brightness), Brightness, "Brightness must be 0..5");
        if (Speed is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(Speed), Speed, "Speed must be 0..5");
        if (Direction is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(Direction), Direction, "Direction must be 0..3");
        if (ColorMode is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(ColorMode), ColorMode, "ColorMode must be 0..255");
    }

    /// <summary>Returns a copy with every field clamped into its legal range (used after loading hand-edited files).</summary>
    public RgbSettings Clamped() => this with
    {
        Mode = Math.Clamp(Mode, 0, ModeCount - 1),
        Brightness = Math.Clamp(Brightness, 0, 5),
        Speed = Math.Clamp(Speed, 0, 5),
        Direction = Math.Clamp(Direction, 0, 3),
        ColorMode = Math.Clamp(ColorMode, 0, 255),
    };
}
