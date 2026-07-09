using Avalonia;
using Avalonia.Data.Converters;

namespace MyBibleApp.Converters;

public static class BibleConverters
{
    // Converts EffectivePoetryLevel → paragraph grid Margin.
    // Level 0 = prose (v1 or non-poetry): full bottom margin, no extra indent.
    // Level 1 = q1 compact: tight bottom margin, base left.
    // Level 2 = q2 compact: tight bottom margin, +24px left indent.
    // Level 3 = q3 compact: tight bottom margin, +48px left indent.
    public static readonly FuncValueConverter<int, Thickness> PoetryParagraphMargin =
        new(level => level == 0
            ? new Thickness(24, 0, 64, 14)
            : new Thickness(24 + (level - 1) * 24, 0, 64, 4));
}
