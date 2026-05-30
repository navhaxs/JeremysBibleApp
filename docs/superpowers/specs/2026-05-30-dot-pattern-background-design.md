# Dotted Page Pattern Background

**Date:** 2026-05-30  
**Status:** Approved

## Overview

Add a toggleable dotted page pattern to the Bible reading area. Dots are rendered behind all content (text, pen strokes). Background color continues to come from the active theme. Toggle persists across sessions.

## Architecture

### AppViewModel (`MyBibleApp/ViewModels/AppViewModel.cs`)

New persisted bool setting, following the `IsDebugMode` pattern exactly:

- Constant: `DotPatternKey = "IsDotPatternEnabled"`
- Backing field: `_isDotPatternEnabled`
- Property: `IsDotPatternEnabled` — setter calls `PersistDotPatternAsync()`
- Method: `PersistDotPatternAsync(bool)` — saves to `ILocalStorageProvider`
- Method: `LoadDotPatternFromStorageAsync()` — reads on startup, same shape as `LoadDebugModeFromStorageAsync()`

### ApplyTheme (`MyBibleApp/Views/MainView.axaml.cs`)

After applying background/foreground overrides, build and store `ThemeDotPatternBrush`:

**Dot color:** Fixed opacity based on theme variant:
- `ThemeVariant.Dark` → `Color.FromArgb(45, 255, 255, 255)` (white, ~18% opacity)
- `ThemeVariant.Light` → `Color.FromArgb(45, 0, 0, 0)` (black, ~18% opacity)

**Brush construction** — `DrawingBrush` with `TileMode.Tile`, `DestinationRect` of 20×20 absolute pixels, containing a `DrawingGroup` with two children:
1. `GeometryDrawing` — `RectangleGeometry(0, 0, 20, 20)` filled with effective background color (`theme.BackgroundOverride` if set, else resolved at runtime from `ThemeBackgroundBrush`)
2. `GeometryDrawing` — `EllipseGeometry` centered at `(10, 10)` with `RadiusX = RadiusY = 1.5`, filled with dot color

Store: `Application.Current.Resources["ThemeDotPatternBrush"] = brush`

**Background color resolution for tile fill:** At `ApplyTheme()` call time, the effective background is:
- `theme.BackgroundOverride` if non-null (sepia, rose, black themes)
- For LightWhite / DarkGrey: read the resolved `ThemeBackgroundBrush` from `Application.Current.Resources` after setting the variant, or use the known default colors (`#FFFFFF` for LightWhite, `#2D2D2D` for DarkGrey)

Simplest approach: add a `Color GetEffectiveBackground(AppTheme theme)` helper on `AppTheme` or inline in `ApplyTheme`.

### MainView.axaml (`MyBibleApp/Views/MainView.axaml`)

**Reading area (`InkAreaGrid`):**

Add a `Border` as the first child of `InkAreaGrid` at `ZIndex="-2"` (below `PenUnderlay` at `ZIndex="-1"`):

```xml
<Border Grid.Row="0"
        ZIndex="-2"
        IsVisible="{Binding AppVM.IsDotPatternEnabled}"
        Background="{DynamicResource ThemeDotPatternBrush}" />
```

Set `ParagraphList` (the `ListBox`) `Background="Transparent"` so the border shows through.

**Menu flyout (`StackPanel` in menu button's `Flyout`):**

Add after the debug mode `ToggleSwitch`:

```xml
<ToggleSwitch OffContent="Page pattern"
              OnContent="Page pattern"
              IsChecked="{Binding AppVM.IsDotPatternEnabled, Mode=TwoWay}" />
```

### AppShellView startup (`MyBibleApp/Views/AppShellView.axaml.cs`)

Call `LoadDotPatternFromStorageAsync()` alongside existing `LoadDebugModeFromStorageAsync()` and `LoadThemeFromStorageAsync()` calls.

## Visual Spec

| Property | Value |
|---|---|
| Tile size | 20 × 20 px |
| Dot radius | 1.5 px |
| Dot center | (10, 10) — one dot per tile |
| Dot opacity | ~18% (alpha 45) |
| Scope | `InkAreaGrid` only (Bible reading area) |
| Z-order | Below pen underlay (ZIndex -2), below ListBox content |

## Files Changed

| File | Change |
|---|---|
| `MyBibleApp/ViewModels/AppViewModel.cs` | Add `IsDotPatternEnabled` property, persist/load methods |
| `MyBibleApp/Views/MainView.axaml.cs` | Extend `ApplyTheme()` with `ThemeDotPatternBrush` construction |
| `MyBibleApp/Views/MainView.axaml` | Add dot pattern `Border`, set ListBox `Background="Transparent"`, add menu toggle |
| `MyBibleApp/Views/AppShellView.axaml.cs` | Call `LoadDotPatternFromStorageAsync()` on startup |
