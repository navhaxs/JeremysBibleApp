# Bible Reading Chart — Canonical Group Color Coding

**Date:** 2026-06-13
**Status:** Approved

## Goal

Style the "My Bible Reading" screen to match the visual language of the reference Bible reading chart — book name labels have a solid colored background per canonical section, and read chapter cells use the matching section color. Unread cells stay neutral. Improves scannability and adds visual delight with zero perf regression.

## Color Groups

| Group | Books (canonical index) | Label bg | Cell color (read) |
|---|---|---|---|
| Pentateuch | Gen–Deut (OT 0–4) | `#4A5E2E` | `#6A8A4A` |
| Historical | Josh–Esther (OT 5–16) | `#7A8A2E` | `#9AAA4A` |
| Poetry/Wisdom | Job–Song (OT 17–21) | `#4E3C82` | `#6E5CA8` |
| Major Prophets | Isa–Dan (OT 22–26) | `#1E3A6E` | `#3A5A90` |
| Minor Prophets | Hos–Mal (OT 27–38) | `#4E5E22` | `#6E7E38` |
| Gospels + Acts | Matt–Acts (NT 0–4) | `#921870` | `#C03898` |
| Paul's Letters | Rom–Phm (NT 5–17) | `#9A4A08` | `#CC6A20` |
| General + Revelation | Heb–Rev (NT 18–26) | `#8A6A00` | `#B89010` |

Label foreground is always a light tint of the label bg (approx +80 lightness). White text on read cells.

## Architecture

### New file: `BibleBookGroups.cs`

Static class. Single method:

```csharp
public static (IBrush LabelBg, IBrush LabelFg, Color CellColor) GetGroupColors(bool isOt, int bookIndex)
```

Returns colors for a book given its testament and 0-based index within that testament. Contains the color lookup table above. Brushes are pre-allocated static readonly instances.

### Modified: `BibleReadingBookEntry.cs`

Add three new readonly properties set in constructor:
- `IBrush LabelBackground`
- `IBrush LabelForeground`
- `Color ReadCellColor`

Constructor receives `bool isOt, int bookIndex`, calls `BibleBookGroups.GetGroupColors`.

### Modified: `BibleReadingViewModel.cs`

When building OT/NT lists, pass `isOt` and per-testament index to `BibleReadingBookEntry` constructor.

### Modified: `ChapterGridControl.cs`

- Add `StyledProperty<Color> BookCellColorProperty`
- Add `private SolidColorBrush? _cachedCellBrush` field
- Override `OnPropertyChanged` to rebuild `_cachedCellBrush` when `BookCellColorProperty` changes
- In `Render()`, use `_cachedCellBrush` for `IsRead` cells instead of `accentBrush`

### Modified: `BibleReadingView.axaml`

In the `DataTemplate` for each book:
- Wrap book name `TextBlock` in a `Border` with `Background="{Binding LabelBackground}"`, `CornerRadius="2"`, `Padding="4,2"`
- Set `TextBlock.Foreground="{Binding LabelForeground}"`
- Add `BookCellColor="{Binding ReadCellColor}"` to `ChapterGridControl`

In `Style Selector="Border.book-card"`:
- Remove `CornerRadius` (avoids GPU clip layer per card on Android, perf improvement)

## What Does NOT Change

- `ChapterGridControl` render loop structure — single pass, no new controls
- Layout structure (WrapPanel, ItemsControl, two-column OT/NT split)
- Sync, storage, ViewModel logic
- Current chapter highlight (accent border stays)
- Hover/pressed visual states

## Performance

No new layout nodes. `ChapterGridControl` already allocates brushes per render; caching `_cachedCellBrush` as a field removes that allocation. Removing `CornerRadius` from `book-card` eliminates per-card GPU clip on Android.
