# Journal Mode Horizontal Scroll — Design Spec

**Date:** 2026-06-06

## Problem

On mobile (narrow viewport), when journal mode is active, the viewport width is often less than the journal's fixed `TextColumnWidthDip`. Both `ParagraphList` and `InkOverlayCanvas` shrink to viewport width, causing text to wrap at a narrower width than when annotations were drawn. Result: pen annotations misalign.

When journal mode is inactive, text wrapping to viewport width is correct and desired behaviour.

## Constraints

- All journals have `TextColumnWidthDip > 0` (guaranteed by the data model).
- `InkOverlayCanvas` and `ParagraphList` are siblings inside `InkAreaGrid`. They must scroll together — scrolling only the ListBox's internal scroll viewer would desync ink from text.
- Vertical scrolling is handled by the ListBox's internal `ScrollViewer` (currently `HorizontalScrollBarVisibility="Disabled"`, `VerticalScrollBarVisibility="Hidden"`). The new outer scroll container must not interfere with vertical scroll.
- `_paragraphList.Bounds.X` drives `_textColumnOffsetX` in `InkOverlayCanvas` (column-relative stroke coords). This offset must remain accurate after horizontal scroll is enabled. Since `_inkAreaGrid.SizeChanged` already calls `UpdateInkTextColumnOffset()`, no extra wiring is needed.

## Solution

### XAML — wrap `InkAreaGrid` in a horizontal `ScrollViewer`

Replace the `InkAreaGrid` at `Grid.Row="2"` with a `ScrollViewer` wrapper at `Grid.Row="2"`, and move `InkAreaGrid` inside it (removing `Grid.Row="2"` from the Grid itself).

```xml
<ScrollViewer
    Grid.Row="2"
    HorizontalScrollBarVisibility="Disabled"
    VerticalScrollBarVisibility="Disabled"
    x:Name="ContentHScrollContainer">
    <Grid
        Background="{DynamicResource ThemeDotPatternBrush}"
        RowDefinitions="*"
        x:Name="InkAreaGrid">
        <!-- InkOverlayCanvas + ParagraphList unchanged -->
    </Grid>
</ScrollViewer>
```

`VerticalScrollBarVisibility="Disabled"` prevents the outer ScrollViewer from consuming vertical scroll events — they fall through to the ListBox's internal ScrollViewer.

### Code-behind — `MainView.axaml.cs`

**New field:**
```csharp
private ScrollViewer? _contentHScrollContainer;
```

**Wire-up** (alongside existing `FindControl` calls):
```csharp
_contentHScrollContainer = this.FindControl<ScrollViewer>("ContentHScrollContainer");
```

**`SetJournalLayout` changes:**

When `layout != null` (journal active — `TextColumnWidthDip` is always > 0):
```csharp
_inkAreaGrid.MinWidth = layout.TextColumnWidthDip;
_contentHScrollContainer!.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
```

When `layout == null` (journal inactive):
```csharp
_inkAreaGrid.MinWidth = 0;
_contentHScrollContainer!.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
```

### Behaviour matrix

| Condition | `InkAreaGrid.MinWidth` | H-scroll |
|---|---|---|
| Journal active, viewport < column width | `TextColumnWidthDip` | Auto (scrollbar appears) |
| Journal active, viewport ≥ column width | `TextColumnWidthDip` | Auto (scrollbar hidden — content fits) |
| Journal inactive | `0` | Disabled |

### Why `MinWidth`, not `Width`

`MinWidth` enforces a floor: the grid is at least `TextColumnWidthDip` wide, but can grow wider on large screens. `Width` would pin it to exactly `TextColumnWidthDip` even on desktop, causing unnecessary horizontal scroll there.

### Desktop impact

On desktop with a wide viewport, `Auto` visibility means no scrollbar appears (content already fits within viewport). Zero UX change on desktop.

## Files Changed

- `MyBibleApp/Views/MainView.axaml` — add `ScrollViewer` wrapper around `InkAreaGrid`
- `MyBibleApp/Views/MainView.axaml.cs` — add `_contentHScrollContainer` field, wire-up, update `SetJournalLayout`
