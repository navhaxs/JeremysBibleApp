using Avalonia;
using Avalonia.Data.Converters;

namespace MyBibleApp.Converters;

public static class BibleConverters
{
    // Converts EffectivePoetryLevel → paragraph grid Margin.
    // Base left/right reading margins live on ParagraphList.Padding (responsive to viewport
    // width, see MainView.UpdateResponsiveRightMargin) — this only adds the poetry-specific
    // extra left indent plus bottom spacing.
    // Level 0 = prose (v1 or non-poetry): full bottom margin, no extra indent.
    // Level 1 = q1 compact: tight bottom margin, no extra indent.
    // Level 2 = q2 compact: tight bottom margin, +24px left indent.
    // Level 3 = q3 compact: tight bottom margin, +48px left indent.
    public static readonly FuncValueConverter<int, Thickness> PoetryParagraphMargin =
        new(level => level == 0
            ? new Thickness(0, 0, 0, 14)
            : new Thickness((level - 1) * 24, 0, 0, 4));
}
