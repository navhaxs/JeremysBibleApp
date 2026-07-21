# Bible Reading: Min-Width + Horizontal Scroll Fallback

**Date:** 2026-06-13  
**Status:** Approved

## Problem

On narrow viewports (e.g. Galaxy S24, ~412px), the two-column OT/NT layout in "My Bible Reading" is squished because `HorizontalScrollBarVisibility="Disabled"` and the inner Grid has no minimum width constraint.

## Solution

Set a minimum content width of 600px on the books Grid. When the viewport is narrower than 600px, horizontal scroll activates. On wider screens, the Grid fills 100% of the available width.

### Why code-behind instead of pure XAML

`ColumnDefinitions="*,*"` inside a `ScrollViewer` with `HorizontalScrollBarVisibility="Auto"` receives infinite available width from the layout system, causing `*` columns to explode. Setting `BooksGrid.Width = Math.Max(600, actualWidth)` explicitly avoids this while also implementing the min-width + fill-to-100% behavior in one step.

## Changes

### `BibleReadingView.axaml`

1. `PanScrollViewer`: change `HorizontalScrollBarVisibility="Disabled"` → `"Auto"`
2. Inner books `Grid`: add `x:Name="BooksGrid"`

### `BibleReadingView.axaml.cs`

3. Subscribe to `SizeChanged` on the UserControl. In handler:
   ```csharp
   BooksGrid.Width = Math.Max(600, e.NewSize.Width);
   ```
   This must also run on initial load (or `OnAttachedToVisualTree`) to set the initial width before first layout pass.

## Behaviour

| Viewport width | Books Grid width | H-scroll |
|---|---|---|
| < 600px (e.g. 412px) | 600px (fixed) | visible |
| ≥ 600px | fills viewport (100%) | hidden |

## Out of Scope

- Changing number of chapter columns per row (e.g. 5-column small mode) — dropped in favour of simpler approach
- Breakpoint-based layout switching
- Any ViewModel changes
