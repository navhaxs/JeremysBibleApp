using System;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using MyBibleApp.Models;

namespace MyBibleApp.Journal.Tests.Generators;

/// <summary>
/// FsCheck Arbitrary instances for ink stroke-related types.
/// </summary>
public static class InkStrokeGenerators
{
    private static readonly string[] HexColors =
    [
        "#FF0000", "#00FF00", "#0000FF", "#FFD700", "#FF6347",
        "#4B0082", "#000000", "#FFFFFF", "#808080", "#FFA500"
    ];

    /// <summary>
    /// Generates a valid StrokePoint with fractional coordinates in range 0-2000.
    /// </summary>
    public static Gen<StrokePoint> PointGen() =>
        Gen.Zip(Gen.Choose(0, 200000), Gen.Choose(0, 200000))
            .Select(t => new StrokePoint(t.Item1 / 100.0, t.Item2 / 100.0));

    /// <summary>
    /// Generates a valid hex color string.
    /// </summary>
    public static Gen<string> ColorGen() => Gen.Elements(HexColors);

    /// <summary>
    /// Generates a valid stroke width (0.5-20.0).
    /// </summary>
    public static Gen<double> StrokeWidthGen() =>
        Gen.Choose(5, 200).Select(i => i / 10.0);

    /// <summary>
    /// Generates a valid JournalInkStroke with 2-50 points.
    /// </summary>
    public static Arbitrary<JournalInkStroke> InkStroke() =>
        Arb.From(InkStrokeGen());

    /// <summary>
    /// Gen for JournalInkStroke.
    /// </summary>
    public static Gen<JournalInkStroke> InkStrokeGen() =>
        from pointCount in Gen.Choose(2, 50)
        from points in Gen.ListOf(PointGen(), pointCount)
        from color in ColorGen()
        from strokeWidth in StrokeWidthGen()
        from isHighlight in Gen.Elements(true, false)
        select new JournalInkStroke
        {
            Id = Guid.NewGuid().ToString(),
            Points = points.ToList(),
            Color = color,
            StrokeWidth = strokeWidth,
            IsHighlight = isHighlight
        };
}
