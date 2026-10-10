namespace Korty.Core.Model;

/// <summary>One of the five key-response levels of the vendor software, with its published timings (from the vendor's own language file).</summary>
public sealed record KeyResponseLevel(int Level, string WiredMs, string Wireless24Ms, string BluetoothMs, string Title);

/// <summary>
/// The keyboard-level settings block that the vendor software sends as ONE feature report (found by static analysis of its
/// settings function): key response level, sleep time, three key disables, Fn switch. The report is always sent in full, exactly
/// like the vendor software does, so every field must hold the value the user wants.
/// </summary>
public sealed record KeyboardSettings
{
    /// <summary>Sleep after idle: 0 never, 1 → 1 min, 2 → 5 min, 3 → 30 min (vendor options "No Sleep / 1min / 5min / 30min").</summary>
    public int SleepLevel { get; init; } = 2;
    /// <summary>Key response (debounce) level 1..5; the vendor software stores the level number itself (UI "3 Level" ↔ value 3).</summary>
    public int KeyResponseLevel { get; init; } = 3;
    public bool DisableWinKey { get; init; }
    public bool DisableAltF4 { get; init; }
    public bool DisableAltTab { get; init; }
    /// <summary>The vendor software sends its stored fn_switch (1) — KORTY sends the same constant.</summary>
    public const int FnSwitch = 1;

    public static readonly IReadOnlyList<string> SleepNames = new[] { "Никогда", "1 минута", "5 минут", "30 минут" };

    public static readonly IReadOnlyList<KeyResponseLevel> Levels = new KeyResponseLevel[]
    {
        new(1, "≈ 2–3 мс", "≈ 5–6 мс", "≈ 12–13 мс", "Самый быстрый отклик"),
        new(2, "≈ 5–6 мс", "≈ 7–9 мс", "≈ 15–16 мс", "Быстрый"),
        new(3, "≈ 8–9 мс", "≈ 10–12 мс", "≈ 18–19 мс", "Сбалансированный"),
        new(4, "≈ 13–14 мс", "≈ 15–17 мс", "≈ 23–24 мс", "Надёжный"),
        new(5, "≈ 17–18 мс", "≈ 19–21 мс", "≈ 27–28 мс", "Самая большая защита от дребезга"),
    };

    public void Validate()
    {
        if (SleepLevel is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(SleepLevel), SleepLevel, "Sleep level must be 0..3");
        if (KeyResponseLevel is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(KeyResponseLevel), KeyResponseLevel, "Key response level must be 1..5");
    }

    public KeyboardSettings Clamped() => this with { SleepLevel = Math.Clamp(SleepLevel, 0, 3), KeyResponseLevel = Math.Clamp(KeyResponseLevel, 1, 5) };
}
