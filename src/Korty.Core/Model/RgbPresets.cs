namespace Korty.Core.Model;

public sealed record RgbPreset(string Name, string Description, RgbSettings Settings);

/// <summary>One-click combinations of the firmware's own effects (mode + colour + speed + brightness). Nothing new is sent to the keyboard — just normal lighting settings.</summary>
public static class RgbPresets
{
    private static RgbPreset P(string name, string desc, int mode, string hex, int br, int sp, int dir = 0, bool rainbow = false) =>
        new(name, desc, new RgbSettings { Mode = mode, Primary = RgbColor.Parse(hex), Brightness = br, Speed = sp, Direction = dir, ColorMode = rainbow ? 1 : 0 });

    public static readonly IReadOnlyList<RgbPreset> All = new[]
    {
        P("Неон", "Дыхание голубого неона", 7, "#00E5FF", 5, 2),
        P("Закат", "Тёплый статичный оранжевый", 1, "#FF6A2B", 4, 3),
        P("Радуга", "Плавный спектр", 8, "#FFFFFF", 5, 3, rainbow: true),
        P("Лава", "Пульсирующий красный", 7, "#FF2D00", 5, 2),
        P("Лес", "Зелёная волна слева направо", 11, "#3DDC97", 4, 2, 0),
        P("Киберпанк", "Розовые пульсации", 17, "#FF2BD6", 5, 3),
        P("Лёд", "Холодная прокрутка", 10, "#6AA9FF", 4, 3, 0),
        P("Звёздная ночь", "Мерцание фиолета", 4, "#B38CFF", 3, 2),
        P("Золотая рябь", "Волны от нажатых клавиш", 15, "#FFB020", 4, 3),
        P("Дождь", "Падающие зелёные капли", 5, "#3DDC97", 4, 3),
        P("Поток", "Радужный поток справа налево", 16, "#FFFFFF", 5, 3, 3, rainbow: true),
        P("Ночник", "Тусклый тёплый свет", 1, "#FFB46B", 2, 3),
        P("Минимал", "Белая подсветка нажатых клавиш", 2, "#FFFFFF", 4, 3),
    };
}
