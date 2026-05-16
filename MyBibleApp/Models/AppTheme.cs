using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Styling;

namespace MyBibleApp.Models;

/// <summary>
/// Describes a selectable app theme: base variant (Light/Dark) plus optional
/// tint colours for the background and foreground.
/// </summary>
public sealed class AppTheme
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required ThemeVariant Variant { get; init; }

    /// <summary>Swatch colour shown in the picker UI.</summary>
    public required Color SwatchColor { get; init; }

    /// <summary>Optional override for the page background.</summary>
    public Color? BackgroundOverride { get; init; }

    /// <summary>Optional override for the main foreground / text colour.</summary>
    public Color? ForegroundOverride { get; init; }

    // ── Predefined themes ────────────────────────────────────────────────────

    public static readonly AppTheme LightWhite = new()
    {
        Id = "light-white",
        Label = "Light",
        Variant = ThemeVariant.Light,
        SwatchColor = Color.Parse("#FFFFFF"),
    };

    public static readonly AppTheme LightSepia = new()
    {
        Id = "light-sepia",
        Label = "Sepia",
        Variant = ThemeVariant.Light,
        SwatchColor = Color.Parse("#F5EDDA"),
        BackgroundOverride = Color.Parse("#F5EDDA"),
        ForegroundOverride = Color.Parse("#3B3228"),
    };

    public static readonly AppTheme LightRose = new()
    {
        Id = "light-rose",
        Label = "Rose",
        Variant = ThemeVariant.Light,
        SwatchColor = Color.Parse("#F8E8E8"),
        BackgroundOverride = Color.Parse("#F8E8E8"),
        ForegroundOverride = Color.Parse("#3B2828"),
    };

    public static readonly AppTheme DarkGrey = new()
    {
        Id = "dark-grey",
        Label = "Dark",
        Variant = ThemeVariant.Dark,
        SwatchColor = Color.Parse("#2D2D2D"),
    };

    public static readonly AppTheme DarkBlack = new()
    {
        Id = "dark-black",
        Label = "Black",
        Variant = ThemeVariant.Dark,
        SwatchColor = Color.Parse("#000000"),
        BackgroundOverride = Color.Parse("#000000"),
        ForegroundOverride = Color.Parse("#E0E0E0"),
    };

    public static IReadOnlyList<AppTheme> All { get; } =
    [
        LightWhite,
        LightSepia,
        LightRose,
        DarkGrey,
        DarkBlack,
    ];

    public static AppTheme GetById(string? id) =>
        All.FirstOrDefault(t => t.Id == id) ?? LightWhite;
}
