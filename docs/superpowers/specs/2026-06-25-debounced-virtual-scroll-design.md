# Debounced Windowed Scrolling with Virtual Heights — Design Spec

**Date:** 2026-06-25

## Problem

Rapid scrolling (thumb drag or fast touch pan) causes jank and sluggishness in two distinct ways:

### 1. Thumb Drag Jank
`CheckWindowExtend` fires on every pointer-move event during thumb drag. Dragging from ch1→ch50 triggers ~49 simultaneous `ChapterEnteredWindow` events, each spawning an async ink DB load and a layout pass. The mass concurrent work saturates the UI thread.

### 2. Pan into Unloaded Territory
`GetVisibleChapterRange` uses `_chapterStartY`, which only contains measured positions for loaded chapters. Panning past the loaded window causes positions to be estimated via `EstimateChapterHeight` (60px × paraCount). When actual chapter height differs from estimate, `ApplyPendingTopCompensation` fires and the visible position jumps. Additionally, the scroll extent only reflects loaded chapters — the scroll thumb position does not represent true position in the book.

---

## Solution Overview

Two features, implemented together:

1. **Thumb drag debounce** — suppress `CheckWindowExtend` and `CheckWindowBounds` during thumb drag; fire a single targeted load on pointer release.

2. **Virtual scroll heights** — maintain estimated heights for ALL chapters in the book. Use a `VirtualScrollPanel` as the `ListBox.ItemsPanel` to provide virtual top/bottom padding to the scroll extent, so the scroll thumb always represents true book position and chapter positions are estimable even when unloaded.

---

## Feature 1: VirtualScrollPanel

### Why a custom Panel (not fake items in `_windowedItems`)
Keeps `_windowedItems` clean — only real `BibleParagraph` objects. No special-case `DataTemplate` or type-switch logic. Spacer heights are layout properties, not data.

### VirtualScrollPanel (`MyBibleApp/Controls/VirtualScrollPanel.cs`)

Custom `Panel` with two `StyledProperty<double>` properties: `TopPadding` and `BottomPadding`.

- `MeasureOverride(availableSize)`: measure each child, return `(maxWidth, TopPadding + sumChildHeights + BottomPadding)`
- `ArrangeOverride(finalSize)`: stack children vertically starting at Y = `TopPadding`
- No virtualization — all children always realized (same as current `StackPanel`)

Setting either property calls `InvalidateMeasure()` to trigger a layout pass.

### AXAML Change (`MainView.axaml`)

```xml
<!-- Before -->
<ItemsPanelTemplate><StackPanel /></ItemsPanelTemplate>

<!-- After -->
<ItemsPanelTemplate><controls:VirtualScrollPanel /></ItemsPanelTemplate>
```

One-line change. Add `xmlns:controls="..."` if not already present for this namespace.

### Code-behind ref

In `EnsureScrollTrackingAttached` (alongside `_paragraphScrollViewer` assignment):

```csharp
_virtualScrollPanel = _paragraphList.GetVisualDescendants()
    .OfType<VirtualScrollPanel>().FirstOrDefault();
```

---

## Feature 2: Virtual Heights Array

### New fields (`MainView.axaml.cs`)

```csharp
private double[] _virtualHeights = Array.Empty<double>(); // 0-based, all chapters in book
private VirtualScrollPanel? _virtualScrollPanel;
private double _topSpacerHeight;
private double _bottomSpacerHeight;
```

### Initialization

`InitializeVirtualHeights()` — called immediately after `_chapterGroups` is fully populated (find call site by searching `_chapterGroups.Clear()` or `.Add`):

```csharp
private void InitializeVirtualHeights()
{
    _virtualHeights = new double[_chapterGroups.Count];
    for (int i = 0; i < _chapterGroups.Count; i++)
        _virtualHeights[i] = EstimateChapterHeight(i + 1);
    _topSpacerHeight = 0;
    _bottomSpacerHeight = _virtualHeights.Sum();
    UpdateSpacers();
}

private void UpdateSpacers()
{
    if (_virtualScrollPanel == null) return;
    _virtualScrollPanel.TopPadding = _topSpacerHeight;
    _virtualScrollPanel.BottomPadding = _bottomSpacerHeight;
}
```

---

## Feature 3: Updated Window Operations

**Key invariant:** `TopPadding + loadedContent + BottomPadding = full virtual book height`

When the window extends/trims, spacers shrink/grow by the chapter's height to maintain total extent stability.

### ExtendWindowDown (ch enters from below, 0-based index `_windowEnd`)

```
_bottomSpacerHeight -= _virtualHeights[_windowEnd]
UpdateSpacers()
// ... existing: add paragraphs, fire ChapterEnteredWindow ...
// In OnParagraphListLayoutUpdated after measure: _virtualHeights[chapter-1] = actual height
```

No scroll compensation needed — content extends downward, below viewport.

### TrimWindowTop (ch at `_windowStart` exits to top spacer)

```
double removedHeight = MeasureChapterHeight(_windowStart + 1) ?? _virtualHeights[_windowStart]
_virtualHeights[_windowStart] = removedHeight   // cache actual
_topSpacerHeight += removedHeight
UpdateSpacers()
// ... existing: remove paragraphs, fire ChapterExitedWindow ...
// DO NOT add to _pendingTopTrimCompensation — TopPadding absorbs the height, no jump occurs
```

No scroll compensation needed. `ch(_windowStart+2)` stays at same absolute Y.

### ExtendWindowUp (ch at `_windowStart - 1` enters from above)

```
_topSpacerHeight -= _virtualHeights[_windowStart - 1]
UpdateSpacers()
// ... existing snapshot + compensation unchanged ...
// _pendingTopExtentBeforeAdd + ApplyPendingTopCompensation still needed (actual vs virtual delta)
// After compensation in OnParagraphListLayoutUpdated: _virtualHeights[chapter-1] = actual height
```

Compensation still needed here because TopPadding shrank by the virtual estimate, but actual rendered height may differ.

### TrimWindowBottom (ch at `_windowEnd - 1` exits to bottom spacer)

```
double removedHeight = MeasureChapterHeight(_windowEnd) ?? _virtualHeights[_windowEnd - 1]
_virtualHeights[_windowEnd - 1] = removedHeight
_bottomSpacerHeight += removedHeight
UpdateSpacers()
// ... existing: remove paragraphs, fire ChapterExitedWindow ...
```

No scroll compensation needed.

---

## Feature 4: Improved GetVisibleChapterRange

Current: returns chapters only within loaded window. When user pans into bottom spacer, returns `_windowEnd - 1` as bottomVisible — causes sequential one-chapter-at-a-time extend even when user jumped 20 chapters.

**Add bottom spacer detection:**

```csharp
// Compute the Y of the bottom of loaded content
double loadedContentBottom = /* bottom Y of last loaded chapter from _chapterStartY */;

if (scrollBottom > loadedContentBottom && _windowEnd < _chapterGroups.Count)
{
    double distanceIntoSpacer = scrollBottom - loadedContentBottom;
    bottomVisible = _windowEnd + FindVirtualChapter(_windowEnd, distanceIntoSpacer);
}
```

**Add top spacer detection** analogously for `scrollTop < _topSpacerHeight`.

### Helper: FindVirtualChapter

```csharp
private int FindVirtualChapter(int startIdx, double targetOffset)
{
    double accumulated = 0;
    for (int i = startIdx; i < _virtualHeights.Length; i++)
    {
        accumulated += _virtualHeights[i];
        if (accumulated > targetOffset) return i - startIdx;
    }
    return _virtualHeights.Length - 1 - startIdx;
}
```

---

## Feature 5: Thumb Drag Debounce

### OnParagraphScrollChanged (line ~524)

Add after existing `_suppressScrollEventsForTabSwitch` check:

```csharp
if (_isProgressTrackDragging)
    return; // suppress windowing during drag; load fires on pointer release
```

### OnProgressTrackPointerReleased (line ~1126)

After clearing `_isProgressTrackDragging`:

```csharp
CheckWindowExtend();
var v = ++_windowCheckVersion;
_ = Task.Delay(50).ContinueWith(_ => {
    if (_windowCheckVersion != v) return;
    Dispatcher.UIThread.Post(() => {
        if (_windowCheckVersion == v) CheckWindowBounds();
    }, DispatcherPriority.Loaded);
});
```

---

## Files Changed

| File | Change |
|---|---|
| `MyBibleApp/Controls/VirtualScrollPanel.cs` | New — custom Panel |
| `MyBibleApp/Views/MainView.axaml` | Swap `StackPanel` → `VirtualScrollPanel` in `ItemsPanelTemplate` |
| `MyBibleApp/Views/MainView.axaml.cs` | Fields, InitializeVirtualHeights, UpdateSpacers, extend/trim updates, GetVisibleChapterRange improvement, thumb drag debounce |

---

## Verification

1. Open Psalms (150 chapters) or any book with 30+ chapters
2. **Thumb drag**: drag scrollbar thumb ch1→ch100 rapidly — smooth, no intermediate chapter-load jank; correct chapter loads on release
3. **Fast pan**: fling through 20+ chapters — scroll position does not jump as chapters load
4. **Thumb accuracy**: drag to 50% scrollbar position → lands near middle chapter of the book
5. **Scroll compensation**: slowly scroll up past an unloaded chapter — text stays stable (ExtendWindowUp compensation still works)
6. **Trim stability**: scroll far forward then back — no oscillation, trims and re-extends cleanly
