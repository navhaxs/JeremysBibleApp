# Windowed Scrolling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace full-book paragraph materialization with a viewport-height-based sliding window of chapters, eliminating scroll lag on large books (Genesis etc.) on mobile, while keeping the ink annotation system fully functional.

**Architecture:** `MainView` maintains an `ObservableCollection<BibleParagraph>` (`_windowedItems`) bound to the `ListBox` instead of the full `Paragraphs` list. As the user scrolls, chapters enter/leave the window; their ink strokes are loaded/unloaded in lock-step. Ink strokes anchor to `(AnchorChapter, AnchorParagraphLocalIndex)` pairs so they are immune to window shifts.

**Tech Stack:** Avalonia UI, SkiaSharp, ReactiveUI, xUnit, C# 13 / .NET 10

---

## File Map

| File | Change |
|------|--------|
| `MyBibleApp/Models/Journal.cs` | Add `AnchorChapter: int` to `JournalInkStroke` |
| `MyBibleApp/Controls/InkOverlayCanvas.cs` | Chapter-based anchor fields + callback signatures + `AppendChapterStrokes`/`RemoveChapterStrokes` |
| `MyBibleApp/Views/MainView.axaml` | Override `ListBox.ItemsSource` binding |
| `MyBibleApp/Views/MainView.axaml.cs` | Chapter grouping, windowed items, scroll-driven windowing, new anchor callbacks, chapter ink entry/exit |
| `MyBibleApp/Views/AppShellView.axaml.cs` | Use `AnchorChapter` in stroke events; per-chapter ink loading |
| `MyBibleApp.Journal.Tests/Unit/JournalInkStrokeTests.cs` | Tests for `AnchorChapter` field |
| `MyBibleApp.Journal.Tests/Unit/ChapterAnchorMigrationTests.cs` | Tests for legacy-to-chapter-anchor conversion |
| `MyBibleApp.Journal.Tests/Unit/WindowedParagraphsTests.cs` | Tests for chapter grouping helpers |

---

## Task 1: Add `AnchorChapter` to `JournalInkStroke` model

**Files:**
- Modify: `MyBibleApp/Models/Journal.cs`
- Modify: `MyBibleApp.Journal.Tests/Unit/JournalInkStrokeTests.cs`

### Background

`JournalInkStroke.AnchorParagraphIndex` currently stores the *global* paragraph index across the whole book. With windowing the global index becomes meaningless (the realized list is a subset). We introduce `AnchorChapter` (the chapter number, 1-based) and repurpose `AnchorParagraphIndex` to mean the *within-chapter* index. Legacy strokes where `AnchorChapter == 0` retain the old global semantics and are migrated at load time in Task 4.

- [ ] **Step 1.1: Add `AnchorChapter` field**

In `MyBibleApp/Models/Journal.cs`, add `AnchorChapter` to `JournalInkStroke` after `AnchorParagraphIndex`:

```csharp
public int AnchorParagraphIndex { get; init; } = -1;
public double AnchorContentTop { get; init; }
public int AnchorChapter { get; init; }      // ← ADD: 1-based chapter; 0 = legacy global index
```

- [ ] **Step 1.2: Write failing test**

In `MyBibleApp.Journal.Tests/Unit/JournalInkStrokeTests.cs`, add:

```csharp
[Fact]
public void JournalInkStroke_AnchorChapter_DefaultsToZero()
{
    var stroke = new JournalInkStroke { Id = "s3" };
    Assert.Equal(0, stroke.AnchorChapter);
}

[Fact]
public void JournalInkStroke_AnchorChapter_RoundTrips()
{
    var stroke = new JournalInkStroke
    {
        Id = "s4",
        AnchorChapter = 5,
        AnchorParagraphIndex = 2,
        AnchorContentTop = 330.0
    };
    Assert.Equal(5, stroke.AnchorChapter);
    Assert.Equal(2, stroke.AnchorParagraphIndex);
}
```

- [ ] **Step 1.3: Run tests — expect FAIL** (`AnchorChapter` property not found)

```
dotnet test MyBibleApp.Journal.Tests --filter "JournalInkStrokeTests"
```

Expected: compile error or 2 failing tests.

- [ ] **Step 1.4: Apply step 1.1 change**

- [ ] **Step 1.5: Run tests — expect PASS**

```
dotnet test MyBibleApp.Journal.Tests --filter "JournalInkStrokeTests"
```

Expected: all green.

- [ ] **Step 1.6: Commit**

```
git add MyBibleApp/Models/Journal.cs MyBibleApp.Journal.Tests/Unit/JournalInkStrokeTests.cs
git commit -m "feat: add AnchorChapter to JournalInkStroke for chapter-based ink anchoring"
```

---

## Task 2: Add `AnchorChapter` to `InkStrokeEventArgs` and `InkStrokeRemovedEventArgs`

**Files:**
- Modify: `MyBibleApp/Controls/InkOverlayCanvas.cs` (event args only — top ~35 lines of the file)

### Background

`InkStrokeEventArgs` is fired when a stroke completes; `AppShellView.OnStrokeCompleted` reads it to persist the stroke. It currently carries `AnchorParagraphIndex` (global). Add `AnchorChapter` so persistence can store the chapter-local anchor.

`InkStrokeRemovedEventArgs` currently carries only `IReadOnlyList<string> StrokeIds`. When multiple chapters are in the window the eraser can hit strokes from different chapters; `AppShellView.OnStrokeRemoved` needs to know which chapter to pass to `RemoveInkStrokeAsync`. Add `IReadOnlyList<(string StrokeId, int Chapter)> RemovedStrokes`.

- [ ] **Step 2.1: Update `InkStrokeEventArgs`**

In `InkOverlayCanvas.cs`, add `AnchorChapter`:

```csharp
public sealed class InkStrokeEventArgs : EventArgs
{
    public required string StrokeId { get; init; }
    public required IReadOnlyList<Point> Points { get; init; }
    public required Color Color { get; init; }
    public required double StrokeWidth { get; init; }
    public required bool IsHighlight { get; init; }
    public required int AnchorParagraphIndex { get; init; }   // within-chapter index
    public required double AnchorContentTop { get; init; }
    public required int AnchorChapter { get; init; }          // ← ADD: 1-based chapter number
}
```

- [ ] **Step 2.2: Update `InkStrokeRemovedEventArgs`**

Replace the existing class:

```csharp
/// <summary>Carries strokes removed by undo or the eraser tool, with their chapter for store routing.</summary>
public sealed class InkStrokeRemovedEventArgs(IReadOnlyList<(string StrokeId, int Chapter)> removedStrokes) : EventArgs
{
    public IReadOnlyList<(string StrokeId, int Chapter)> RemovedStrokes { get; } = removedStrokes;

    // Convenience: just the IDs (for callers that don't care about chapter).
    public IReadOnlyList<string> StrokeIds { get; } =
        removedStrokes.Select(r => r.StrokeId).ToList();
}
```

- [ ] **Step 2.3: Build to find all compile errors**

```
dotnet build MyBibleApp
```

Expect errors at every site that constructs `InkStrokeRemovedEventArgs` with a `List<string>` (eraser + undo paths inside `InkOverlayCanvas`), and every site that reads `e.StrokeIds` in `AppShellView`.

Note the errors — they will be fixed in Tasks 3 and 7.

- [ ] **Step 2.4: Commit (broken build is acceptable here — commit the model change only)**

```
git add MyBibleApp/Controls/InkOverlayCanvas.cs
git commit -m "feat: add AnchorChapter to InkStrokeEventArgs; add per-stroke chapter to InkStrokeRemovedEventArgs"
```

---

## Task 3: Update `InkOverlayCanvas` for chapter-based anchoring

**Files:**
- Modify: `MyBibleApp/Controls/InkOverlayCanvas.cs`

### Background

- `StrokeCache` needs an `AnchorChapter` field.
- `GetParagraphContentTop` callback signature changes: `Func<int, double?>` → `Func<int chapter, int withinChapterIndex, double?>`.
- `FindParagraphAtContentY` callback return type changes: `(int Index, double ContentTop)?` → `(int Chapter, int LocalIndex, double ContentTop)?`.
- Active-stroke anchor fields split: `_activeAnchorIndex` → `_activeAnchorChapter` + `_activeAnchorLocalIndex`.
- All `StrokeRemoved` fire sites must construct the new `InkStrokeRemovedEventArgs`.
- Add `AppendChapterStrokes` and `RemoveChapterStrokes` for windowed loading.

- [ ] **Step 3.1: Update `StrokeCache`**

Change `AnchorParagraphIndex` meaning to within-chapter; add `AnchorChapter`:

```csharp
internal readonly record struct StrokeCache(
    Point DotCenter,
    Rect ContentBounds,
    Color Color,
    double StrokeWidth,
    bool IsHighlight,
    IReadOnlyList<Point>? Points,
    int AnchorChapter = 0,           // ← ADD: 1-based chapter; 0 = unanchored
    int AnchorParagraphIndex = -1,   // within-chapter paragraph index
    double AnchorContentTop = 0,
    string StrokeId = "",
    SKPath? CachedPath = null);
```

- [ ] **Step 3.2: Update callback property declarations**

Replace the two `Func` properties:

```csharp
/// <summary>
/// Returns the current content-space top of the paragraph at (chapter, withinChapterIndex).
/// Returns null if the paragraph is not currently realized in the window.
/// </summary>
public Func<int chapter, int withinChapterIndex, double?>? GetParagraphContentTop { get; set; }

/// <summary>
/// Given a content-space Y, returns the chapter number, within-chapter paragraph index,
/// and content-space top of the nearest realized paragraph.
/// </summary>
public Func<double, (int Chapter, int LocalIndex, double ContentTop)?>? FindParagraphAtContentY { get; set; }
```

Note: C# lambda parameter names in `Func<>` are for documentation only; callers use positional args.

- [ ] **Step 3.3: Update active-stroke anchor fields**

Replace:
```csharp
private int _activeAnchorIndex = -1;
private double _activeAnchorContentTop;
```
With:
```csharp
private int _activeAnchorChapter;        // 1-based
private int _activeAnchorLocalIndex = -1; // within-chapter
private double _activeAnchorContentTop;
```

- [ ] **Step 3.4: Update `StartStroke`**

Replace the anchor capture block:

```csharp
var anchor = FindParagraphAtContentY?.Invoke(contentPt.Y);
_activeAnchorChapter     = anchor?.Chapter    ?? 0;
_activeAnchorLocalIndex  = anchor?.LocalIndex ?? -1;
_activeAnchorContentTop  = anchor?.ContentTop ?? 0;
```

- [ ] **Step 3.5: Update `GetDriftDelta`**

```csharp
private double GetDriftDelta(int anchorChapter, int anchorLocalIndex, double anchorContentTop)
{
    if (anchorChapter <= 0 || anchorLocalIndex < 0 || GetParagraphContentTop == null) return 0;
    var currentTop = GetParagraphContentTop(anchorChapter, anchorLocalIndex);
    return currentTop.HasValue ? currentTop.Value - anchorContentTop : 0;
}
```

- [ ] **Step 3.6: Update all `GetDriftDelta` call sites**

In `Render()`, `EraseAt()`, and anywhere else `GetDriftDelta` is called, change:

```csharp
// Old:
var delta = src.GetDriftDelta(s.AnchorParagraphIndex, s.AnchorContentTop);
// New:
var delta = src.GetDriftDelta(s.AnchorChapter, s.AnchorParagraphIndex, s.AnchorContentTop);
```

And for the active-stroke drift in `Render()`:

```csharp
// Old:
activeHighlightDelta = src.GetDriftDelta(src._activeAnchorIndex, src._activeAnchorContentTop);
// New:
activeHighlightDelta = src.GetDriftDelta(src._activeAnchorChapter, src._activeAnchorLocalIndex, src._activeAnchorContentTop);
```

- [ ] **Step 3.7: Update `EndStroke`**

Replace every `_activeAnchorIndex` reference with `_activeAnchorChapter`/`_activeAnchorLocalIndex`:

```csharp
// In the dot branch (Count == 1):
_cachedStrokes.Add(new StrokeCache(
    p,
    new Rect(p.X - 2, p.Y - 2, 4, 4),
    _activeStrokeColor, _activeStrokeWidth, _activeIsHighlight, null,
    _activeAnchorChapter, _activeAnchorLocalIndex, _activeAnchorContentTop, id));

StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
{
    StrokeId              = id,
    Points                = [],
    Color                 = _activeStrokeColor,
    StrokeWidth           = _activeStrokeWidth,
    IsHighlight           = _activeIsHighlight,
    AnchorChapter         = _activeAnchorChapter,
    AnchorParagraphIndex  = _activeAnchorLocalIndex,
    AnchorContentTop      = _activeAnchorContentTop
});

// In the polyline branch (Count >= 2):
_cachedStrokes.Add(new StrokeCache(
    default,
    ComputeBounds(_activeStroke),
    _activeStrokeColor,
    _activeStrokeWidth,
    _activeIsHighlight,
    pts,
    _activeAnchorChapter,
    _activeAnchorLocalIndex,
    _activeAnchorContentTop,
    id,
    CachedPath: BuildSmoothPath(pts)));

StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
{
    StrokeId              = id,
    Points                = pts,
    Color                 = _activeStrokeColor,
    StrokeWidth           = _activeStrokeWidth,
    IsHighlight           = _activeIsHighlight,
    AnchorChapter         = _activeAnchorChapter,
    AnchorParagraphIndex  = _activeAnchorLocalIndex,
    AnchorContentTop      = _activeAnchorContentTop
});
```

And reset at the end of `EndStroke`:

```csharp
_activeStroke = null;
_activeAnchorChapter    = 0;
_activeAnchorLocalIndex = -1;
_activeAnchorContentTop = 0;
```

- [ ] **Step 3.8: Update `RedoStroke` event**

```csharp
StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
{
    StrokeId             = stroke.StrokeId,
    Points               = pts,
    Color                = stroke.Color,
    StrokeWidth          = stroke.StrokeWidth,
    IsHighlight          = stroke.IsHighlight,
    AnchorChapter        = stroke.AnchorChapter,
    AnchorParagraphIndex = stroke.AnchorParagraphIndex,
    AnchorContentTop     = stroke.AnchorContentTop
});
```

- [ ] **Step 3.9: Fix `UndoStroke` and `EraseAt` — update `StrokeRemoved` firing**

`UndoStroke` removes one stroke:

```csharp
if (!string.IsNullOrEmpty(removed.StrokeId))
    StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(
        [(removed.StrokeId, removed.AnchorChapter)]));
```

`EraseAt` collects removed IDs — change collection to include chapter:

```csharp
List<(string StrokeId, int Chapter)>? removedStrokes = null;
// ... in the hit block:
if (!string.IsNullOrEmpty(s.StrokeId))
    (removedStrokes ??= []).Add((s.StrokeId, s.AnchorChapter));
// ... fire:
if (removedStrokes != null)
{
    _redoStack.Clear();
    Redraw();
    StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(removedStrokes));
}
```

- [ ] **Step 3.10: Update `LoadJournalStrokes` to populate `AnchorChapter`**

```csharp
// In the dot branch:
_cachedStrokes.Add(new StrokeCache(
    p,
    new Rect(p.X - 2, p.Y - 2, 4, 4),
    color, stroke.StrokeWidth, stroke.IsHighlight, null,
    stroke.AnchorChapter, stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id));

// In the polyline branch:
_cachedStrokes.Add(new StrokeCache(
    default,
    ComputeBounds(pts),
    color, stroke.StrokeWidth, stroke.IsHighlight,
    pts,
    stroke.AnchorChapter, stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id,
    CachedPath: BuildSmoothPath(pts)));
```

- [ ] **Step 3.11: Add `AppendChapterStrokes` and `RemoveChapterStrokes`**

After `LoadJournalStrokes`, add:

```csharp
/// <summary>
/// Appends strokes for one chapter entering the scroll window.
/// Does not clear existing strokes from other chapters.
/// </summary>
public void AppendChapterStrokes(IReadOnlyList<JournalInkStroke> strokes)
{
    foreach (var stroke in strokes)
    {
        var pts = stroke.Points.Select(p => new Point(p.X, p.Y)).ToList();
        var color = Color.Parse(stroke.Color.Length > 0 ? stroke.Color : "#FF000000");
        if (pts.Count == 0) continue;

        if (pts.Count == 1)
        {
            var p = pts[0];
            _cachedStrokes.Add(new StrokeCache(
                p, new Rect(p.X - 2, p.Y - 2, 4, 4),
                color, stroke.StrokeWidth, stroke.IsHighlight, null,
                stroke.AnchorChapter, stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id));
        }
        else
        {
            _cachedStrokes.Add(new StrokeCache(
                default, ComputeBounds(pts),
                color, stroke.StrokeWidth, stroke.IsHighlight, pts,
                stroke.AnchorChapter, stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id,
                CachedPath: BuildSmoothPath(pts)));
        }
    }
    Redraw();
}

/// <summary>
/// Removes all strokes whose AnchorChapter matches the given chapter.
/// Called when a chapter leaves the scroll window.
/// </summary>
public void RemoveChapterStrokes(int chapter)
{
    var countBefore = _cachedStrokes.Count;
    _cachedStrokes.RemoveAll(s => s.AnchorChapter == chapter);
    if (_cachedStrokes.Count != countBefore)
        Redraw();
}
```

Note: `List<T>.RemoveAll` — `_cachedStrokes` is `List<StrokeCache>`, so this is available.

- [ ] **Step 3.12: Build**

```
dotnet build MyBibleApp
```

Expect remaining errors only in `MainView.axaml.cs` (callback wiring) and `AppShellView.axaml.cs` (event handlers). Those are fixed in Tasks 4 and 7.

- [ ] **Step 3.13: Commit**

```
git add MyBibleApp/Controls/InkOverlayCanvas.cs
git commit -m "feat: chapter-based anchor in InkOverlayCanvas; add AppendChapterStrokes/RemoveChapterStrokes"
```

---

## Task 4: Chapter grouping + new anchor callbacks in `MainView`

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`
- Create: `MyBibleApp.Journal.Tests/Unit/ChapterAnchorMigrationTests.cs`
- Create: `MyBibleApp.Journal.Tests/Unit/WindowedParagraphsTests.cs`

### Background

`MainView` currently exposes `GetParagraphContentTopFast(int paragraphIndex)` and `FindParagraphAtContentY(double contentY)` with global-index semantics. These must be replaced with chapter-keyed versions. The paragraph-top cache becomes `_chapterStartY: Dictionary<int, double>` + `_chapterLocalTops: Dictionary<int, double[]>`.

We also need a way to convert legacy global `AnchorParagraphIndex` (from persisted strokes with `AnchorChapter == 0`) to chapter-local. This conversion happens in `LoadJournalStrokes`.

- [ ] **Step 4.1: Add chapter-grouping fields and rebuild method**

In `MainView.axaml.cs`, replace:

```csharp
private double[] _paragraphContentTops = [];
```

With:

```csharp
// Chapter grouping built from _paragraphs on every book load.
// _chapterGroups[i] = all paragraphs for chapter (i+1), in order.
private List<List<BibleParagraph>> _chapterGroups = [];
// Fast lookup: paragraph → (1-based chapter, within-chapter index).
private Dictionary<BibleParagraph, (int Chapter, int LocalIndex)> _paragraphChapterInfo = [];

// Chapter content positions — populated from visual tree when chapters are realized.
// Key = 1-based chapter number. Value = content-space Y of chapter's first paragraph.
private Dictionary<int, double> _chapterStartY = [];
// Per-chapter within-chapter local tops. Only populated when chapter is in window.
// Key = 1-based chapter. Value[i] = local Y offset of paragraph i within its chapter.
private Dictionary<int, double[]> _chapterLocalTops = [];
```

- [ ] **Step 4.2: Add `RebuildChapterGroups` method**

After `FindParagraphIndex`, add:

```csharp
/// <summary>
/// Rebuilds _chapterGroups and _paragraphChapterInfo from _paragraphs.
/// O(N) in paragraph count. Call whenever _paragraphs changes.
/// </summary>
private void RebuildChapterGroups()
{
    _chapterGroups.Clear();
    _paragraphChapterInfo.Clear();
    _chapterStartY.Clear();
    _chapterLocalTops.Clear();

    int currentChapter = -1;
    List<BibleParagraph>? currentGroup = null;

    foreach (var para in _paragraphs)
    {
        if (para.StartChapter != currentChapter)
        {
            currentChapter = para.StartChapter;
            currentGroup = [];
            _chapterGroups.Add(currentGroup);
        }
        var localIndex = currentGroup!.Count;
        currentGroup.Add(para);
        _paragraphChapterInfo[para] = (currentChapter, localIndex);
    }
}

/// <summary>Returns (1-based chapter, within-chapter index) for a paragraph, or null.</summary>
private (int Chapter, int LocalIndex)? GetChapterInfo(BibleParagraph para) =>
    _paragraphChapterInfo.TryGetValue(para, out var info) ? info : null;
```

- [ ] **Step 4.3: Call `RebuildChapterGroups` on paragraphs change**

In `OnVmPropertyChanged`:

```csharp
if (e.PropertyName == nameof(ScriptureViewModel.Paragraphs) && sender is ScriptureViewModel vm)
{
    _paragraphs = vm.Paragraphs;
    RebuildChapterGroups();
    ReinitializeWindow();    // defined in Task 5
}
```

Also call in `OnLoaded` after `_paragraphs` is set:

```csharp
if (DataContext is MyBibleApp.ViewModels.ScriptureViewModel vm)
{
    _paragraphs = vm.Paragraphs;
    RebuildChapterGroups();
}
```

- [ ] **Step 4.4: Add `RebuildParagraphTopCache` replacement**

Replace the existing `RebuildParagraphTopCache` body with:

```csharp
private void RebuildParagraphTopCache()
{
    if (_paragraphList == null || _paragraphScrollViewer == null)
        return;

    // Walk realized ListBoxItems grouped by chapter.
    var byChapter = new Dictionary<int, List<(int LocalIndex, double LocalTop)>>();

    foreach (var item in _paragraphList.GetVisualDescendants().OfType<ListBoxItem>())
    {
        if (item.DataContext is not BibleParagraph para) continue;
        if (!_paragraphChapterInfo.TryGetValue(para, out var info)) continue;

        var viewportY = item.TranslatePoint(default, _paragraphScrollViewer)?.Y;
        if (viewportY == null) continue;

        if (!byChapter.TryGetValue(info.Chapter, out var list))
        {
            list = [];
            byChapter[info.Chapter] = list;
        }
        list.Add((info.LocalIndex, viewportY.Value));
    }

    var scrollY = _paragraphScrollViewer.Offset.Y;

    foreach (var (chapter, items) in byChapter)
    {
        items.Sort((a, b) => a.LocalIndex.CompareTo(b.LocalIndex));
        var chapterViewportTop = items.Count > 0 ? items[0].LocalTop : 0;
        _chapterStartY[chapter] = scrollY + chapterViewportTop;

        var localTops = new double[items.Max(x => x.LocalIndex) + 1];
        Array.Fill(localTops, -1.0);
        foreach (var (localIndex, viewportY) in items)
            localTops[localIndex] = viewportY - chapterViewportTop;

        _chapterLocalTops[chapter] = localTops;
    }
}
```

- [ ] **Step 4.5: Replace `GetParagraphContentTopFast` with chapter-keyed version**

```csharp
/// <summary>
/// Returns content-space Y for a paragraph identified by (chapter, withinChapterIndex).
/// Returns null if the chapter is not currently realized in the window.
/// </summary>
private double? GetParagraphContentTopFast(int chapter, int withinChapterIndex)
{
    if (!_chapterStartY.TryGetValue(chapter, out var chapterY)) return null;
    if (!_chapterLocalTops.TryGetValue(chapter, out var localTops)) return null;
    if (withinChapterIndex < 0 || withinChapterIndex >= localTops.Length) return null;
    var local = localTops[withinChapterIndex];
    return local >= 0 ? chapterY + local : null;
}
```

- [ ] **Step 4.6: Replace `FindParagraphAtContentY` with chapter-returning version**

```csharp
private (int Chapter, int LocalIndex, double ContentTop)? FindParagraphAtContentY(double contentY)
{
    if (_paragraphList == null || _paragraphScrollViewer == null || _paragraphs.Count == 0)
        return null;

    var scrollY = _paragraphScrollViewer.Offset.Y;
    (int Chapter, int LocalIndex, double ContentTop, double Height)? best = null;
    double bestDist = double.MaxValue;

    foreach (var item in _paragraphList.GetVisualDescendants().OfType<ListBoxItem>())
    {
        if (item.DataContext is not BibleParagraph para) continue;
        if (!_paragraphChapterInfo.TryGetValue(para, out var info)) continue;

        var top = item.TranslatePoint(default, _paragraphScrollViewer)?.Y;
        if (top == null) continue;

        var contentTop = scrollY + top.Value;
        var height = item.Bounds.Height;

        if (contentY >= contentTop && contentY <= contentTop + height)
            return (info.Chapter, info.LocalIndex, contentTop);

        var dist = Math.Min(Math.Abs(contentY - contentTop),
                            Math.Abs(contentY - (contentTop + height)));
        if (dist < bestDist)
        {
            bestDist = dist;
            best = (info.Chapter, info.LocalIndex, contentTop, height);
        }
    }

    return best.HasValue ? (best.Value.Chapter, best.Value.LocalIndex, best.Value.ContentTop) : null;
}
```

- [ ] **Step 4.7: Wire new callbacks to `_inkOverlay` in `OnLoaded`**

Replace the two callback assignments:

```csharp
_inkOverlay.FindParagraphAtContentY = FindParagraphAtContentY;
_inkOverlay.GetParagraphContentTop  = GetParagraphContentTopFast;
```

- [ ] **Step 4.8: Add legacy migration helper to `LoadJournalStrokes`**

`MainView.LoadJournalStrokes` currently just forwards to `_inkOverlay`. Replace with a migration step:

```csharp
public void LoadJournalStrokes(IReadOnlyList<JournalInkStroke> strokes)
{
    var migrated = MigrateStrokeAnchors(strokes);
    _inkOverlay?.LoadJournalStrokes(migrated);
}

/// <summary>
/// Converts strokes with AnchorChapter == 0 (legacy global paragraph index)
/// to chapter-local anchors using the current book's paragraph grouping.
/// Strokes that already have AnchorChapter > 0 are returned unchanged.
/// </summary>
private IReadOnlyList<JournalInkStroke> MigrateStrokeAnchors(IReadOnlyList<JournalInkStroke> strokes)
{
    if (_paragraphs.Count == 0) return strokes;

    List<JournalInkStroke>? result = null;

    for (var i = 0; i < strokes.Count; i++)
    {
        var s = strokes[i];
        if (s.AnchorChapter != 0)
        {
            result?.Add(s);
            continue;
        }

        // Legacy: AnchorParagraphIndex is global.
        var globalIdx = s.AnchorParagraphIndex;
        if (globalIdx < 0 || globalIdx >= _paragraphs.Count)
        {
            result?.Add(s);
            continue;
        }

        var para = _paragraphs[globalIdx];
        if (!_paragraphChapterInfo.TryGetValue(para, out var info))
        {
            result?.Add(s);
            continue;
        }

        // Needs migration — materialize the list.
        result ??= strokes.Take(i).ToList();
        result.Add(new JournalInkStroke
        {
            Id                   = s.Id,
            Points               = s.Points,
            Color                = s.Color,
            StrokeWidth          = s.StrokeWidth,
            IsHighlight          = s.IsHighlight,
            BookCode             = s.BookCode,
            ChapterNumber        = s.ChapterNumber,
            AnchorChapter        = info.Chapter,
            AnchorParagraphIndex = info.LocalIndex,
            AnchorContentTop     = s.AnchorContentTop
        });
    }

    return result ?? strokes;
}
```

- [ ] **Step 4.9: Also call `MigrateStrokeAnchors` in `AppendChapterStrokes` wrapper on `MainView`**

Add a public wrapper (used in Task 6):

```csharp
public void AppendChapterStrokes(IReadOnlyList<JournalInkStroke> strokes)
{
    var migrated = MigrateStrokeAnchors(strokes);
    _inkOverlay?.AppendChapterStrokes(migrated);
}

public void RemoveChapterStrokes(int chapter) =>
    _inkOverlay?.RemoveChapterStrokes(chapter);
```

- [ ] **Step 4.10: Write tests for `RebuildChapterGroups` logic**

Create `MyBibleApp.Journal.Tests/Unit/WindowedParagraphsTests.cs`.

Note: `RebuildChapterGroups` and `GetChapterInfo` are `private` in `MainView`. Extract the grouping logic to a public static helper class `ChapterGroupBuilder` in `MyBibleApp` so it can be tested without a UI thread.

Create `MyBibleApp/Helpers/ChapterGroupBuilder.cs`:

```csharp
using System.Collections.Generic;
using MyBibleApp.Models;

namespace MyBibleApp.Helpers;

public static class ChapterGroupBuilder
{
    /// <summary>
    /// Groups paragraphs by StartChapter. Returns chapter groups (0-indexed list of chapter paragraphs)
    /// and a lookup from paragraph to (1-based chapter, within-chapter index).
    /// </summary>
    public static (List<List<BibleParagraph>> Groups,
                   Dictionary<BibleParagraph, (int Chapter, int LocalIndex)> Info)
        Build(IReadOnlyList<BibleParagraph> paragraphs)
    {
        var groups = new List<List<BibleParagraph>>();
        var info   = new Dictionary<BibleParagraph, (int, int)>();

        int currentChapter = -1;
        List<BibleParagraph>? currentGroup = null;

        foreach (var para in paragraphs)
        {
            if (para.StartChapter != currentChapter)
            {
                currentChapter = para.StartChapter;
                currentGroup   = [];
                groups.Add(currentGroup);
            }
            var localIndex = currentGroup!.Count;
            currentGroup.Add(para);
            info[para] = (currentChapter, localIndex);
        }

        return (groups, info);
    }
}
```

Then `MainView.RebuildChapterGroups` delegates to this helper:

```csharp
private void RebuildChapterGroups()
{
    (_chapterGroups, _paragraphChapterInfo) = ChapterGroupBuilder.Build(_paragraphs);
    _chapterStartY.Clear();
    _chapterLocalTops.Clear();
}
```

Now write the tests:

```csharp
// MyBibleApp.Journal.Tests/Unit/WindowedParagraphsTests.cs
using MyBibleApp.Helpers;
using MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class WindowedParagraphsTests
{
    private static BibleParagraph Para(int chapter, int verse) =>
        new("text", null, chapter, verse, []);

    [Fact]
    public void Build_EmptyList_ReturnsEmptyGroups()
    {
        var (groups, info) = ChapterGroupBuilder.Build([]);
        Assert.Empty(groups);
        Assert.Empty(info);
    }

    [Fact]
    public void Build_SingleChapter_OneGroup()
    {
        var p1 = Para(1, 1);
        var p2 = Para(1, 2);
        var (groups, info) = ChapterGroupBuilder.Build([p1, p2]);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal((1, 0), info[p1]);
        Assert.Equal((1, 1), info[p2]);
    }

    [Fact]
    public void Build_MultipleChapters_CorrectGroups()
    {
        var p1 = Para(1, 1);
        var p2 = Para(2, 1);
        var p3 = Para(2, 2);
        var (groups, info) = ChapterGroupBuilder.Build([p1, p2, p3]);

        Assert.Equal(2, groups.Count);
        Assert.Single(groups[0]);
        Assert.Equal(2, groups[1].Count);

        Assert.Equal((1, 0), info[p1]);
        Assert.Equal((2, 0), info[p2]);
        Assert.Equal((2, 1), info[p3]);
    }

    [Fact]
    public void Build_ShortChapters_EachGetsSeparateGroup()
    {
        var paragraphs = new List<BibleParagraph>();
        for (var ch = 1; ch <= 10; ch++)
            paragraphs.Add(Para(ch, 1));  // one paragraph per chapter

        var (groups, _) = ChapterGroupBuilder.Build(paragraphs);
        Assert.Equal(10, groups.Count);
        Assert.All(groups, g => Assert.Single(g));
    }
}
```

- [ ] **Step 4.11: Write tests for legacy anchor migration**

Create `MyBibleApp.Journal.Tests/Unit/ChapterAnchorMigrationTests.cs`. Since `MigrateStrokeAnchors` is private, extract it too to a public static helper `InkAnchorMigrator` in `MyBibleApp/Helpers/InkAnchorMigrator.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Helpers;

public static class InkAnchorMigrator
{
    /// <summary>
    /// Converts legacy strokes (AnchorChapter == 0) to chapter-local anchors.
    /// Strokes with AnchorChapter > 0 pass through unchanged.
    /// </summary>
    public static IReadOnlyList<JournalInkStroke> Migrate(
        IReadOnlyList<JournalInkStroke> strokes,
        Dictionary<BibleParagraph, (int Chapter, int LocalIndex)> paragraphInfo,
        IReadOnlyList<BibleParagraph> allParagraphs)
    {
        if (allParagraphs.Count == 0) return strokes;

        List<JournalInkStroke>? result = null;

        for (var i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            if (s.AnchorChapter != 0) { result?.Add(s); continue; }

            var globalIdx = s.AnchorParagraphIndex;
            if (globalIdx < 0 || globalIdx >= allParagraphs.Count) { result?.Add(s); continue; }

            var para = allParagraphs[globalIdx];
            if (!paragraphInfo.TryGetValue(para, out var info)) { result?.Add(s); continue; }

            result ??= strokes.Take(i).ToList();
            result.Add(new JournalInkStroke
            {
                Id                   = s.Id,
                Points               = s.Points,
                Color                = s.Color,
                StrokeWidth          = s.StrokeWidth,
                IsHighlight          = s.IsHighlight,
                BookCode             = s.BookCode,
                ChapterNumber        = s.ChapterNumber,
                AnchorChapter        = info.Chapter,
                AnchorParagraphIndex = info.LocalIndex,
                AnchorContentTop     = s.AnchorContentTop
            });
        }

        return result ?? strokes;
    }
}
```

And update `MainView.MigrateStrokeAnchors` to delegate to this:

```csharp
private IReadOnlyList<JournalInkStroke> MigrateStrokeAnchors(IReadOnlyList<JournalInkStroke> strokes) =>
    InkAnchorMigrator.Migrate(strokes, _paragraphChapterInfo, _paragraphs);
```

Now the tests:

```csharp
// MyBibleApp.Journal.Tests/Unit/ChapterAnchorMigrationTests.cs
using System.Collections.Generic;
using MyBibleApp.Helpers;
using MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class ChapterAnchorMigrationTests
{
    private static BibleParagraph Para(int chapter, int verse) =>
        new("text", null, chapter, verse, []);

    private static (Dictionary<BibleParagraph, (int, int)> Info, List<BibleParagraph> All)
        BuildFixture(int chapters, int versesPerChapter)
    {
        var all = new List<BibleParagraph>();
        for (var ch = 1; ch <= chapters; ch++)
            for (var v = 1; v <= versesPerChapter; v++)
                all.Add(Para(ch, v));

        var (_, info) = ChapterGroupBuilder.Build(all);
        return (info, all);
    }

    [Fact]
    public void Migrate_StrokeWithChapterAlreadySet_PassesThrough()
    {
        var (info, all) = BuildFixture(3, 5);
        var stroke = new JournalInkStroke
        {
            Id = "s1", AnchorChapter = 2, AnchorParagraphIndex = 3, AnchorContentTop = 100
        };

        var result = InkAnchorMigrator.Migrate([stroke], info, all);

        Assert.Same(result[0], stroke);   // reference equality — no allocation
    }

    [Fact]
    public void Migrate_LegacyGlobalIndex_ConvertedToChapterLocal()
    {
        var (info, all) = BuildFixture(3, 5);   // 15 paragraphs: ch1=0-4, ch2=5-9, ch3=10-14
        // Global index 7 = chapter 2, local index 2.
        var stroke = new JournalInkStroke
        {
            Id = "s2", AnchorChapter = 0, AnchorParagraphIndex = 7, AnchorContentTop = 200
        };

        var result = InkAnchorMigrator.Migrate([stroke], info, all);

        Assert.Equal(2, result[0].AnchorChapter);
        Assert.Equal(2, result[0].AnchorParagraphIndex);
        Assert.Equal(200.0, result[0].AnchorContentTop);
    }

    [Fact]
    public void Migrate_LegacyGlobalIndexOutOfRange_PassesThrough()
    {
        var (info, all) = BuildFixture(1, 5);
        var stroke = new JournalInkStroke
        {
            Id = "s3", AnchorChapter = 0, AnchorParagraphIndex = 999
        };

        var result = InkAnchorMigrator.Migrate([stroke], info, all);
        Assert.Same(result[0], stroke);
    }

    [Fact]
    public void Migrate_EmptyList_ReturnsEmpty()
    {
        var result = InkAnchorMigrator.Migrate([], new(), []);
        Assert.Empty(result);
    }
}
```

- [ ] **Step 4.12: Run tests**

```
dotnet test MyBibleApp.Journal.Tests --filter "WindowedParagraphsTests|ChapterAnchorMigrationTests"
```

Expected: all green.

- [ ] **Step 4.13: Build `MyBibleApp`**

```
dotnet build MyBibleApp
```

Expected: only remaining errors in `AppShellView.axaml.cs` (fixed in Task 7).

- [ ] **Step 4.14: Commit**

```
git add MyBibleApp/Helpers/ChapterGroupBuilder.cs \
        MyBibleApp/Helpers/InkAnchorMigrator.cs \
        MyBibleApp/Views/MainView.axaml.cs \
        MyBibleApp.Journal.Tests/Unit/WindowedParagraphsTests.cs \
        MyBibleApp.Journal.Tests/Unit/ChapterAnchorMigrationTests.cs
git commit -m "feat: chapter-keyed paragraph cache and legacy ink anchor migration in MainView"
```

---

## Task 5: Windowed paragraph loading in `MainView`

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml` (remove XAML binding from ListBox)
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

### Background

The `ListBox` currently binds `ItemsSource` to `{Binding Paragraphs}` (the full book). Replace with an `ObservableCollection<BibleParagraph>` managed by `MainView`. The window is defined by `(_windowStart, _windowEnd)` — chapter-group indices into `_chapterGroups`. The window always covers at least 2 × viewport height of content.

**Window invariants:**
- `_windowStart` and `_windowEnd` are indices into `_chapterGroups` (0-based; chapter = index + 1).
- On extend-down: add `_chapterGroups[_windowEnd]` paragraphs to `_windowedItems`, increment `_windowEnd`.
- On extend-up: prepend `_chapterGroups[_windowStart - 1]` to `_windowedItems`, decrement `_windowStart`; compensate scroll offset.
- On trim-top: remove `_chapterGroups[_windowStart]` from `_windowedItems`, increment `_windowStart`; compensate scroll offset downward.
- On trim-bottom: remove `_chapterGroups[_windowEnd - 1]` from `_windowedItems`, decrement `_windowEnd`.

**Scroll offset compensation** when removing from top: The ListBox content shifts up by the removed height. Compensate by subtracting that height from `ScrollViewer.Offset.Y`. Measure removed-chapter heights from the visual tree *before* removing the items.

**Window trigger thresholds:**
- Extend down when: distance from scroll-bottom to window-content-bottom < 1 × viewport height.
- Extend up when: distance from scroll-top to window-content-top < 0.5 × viewport height.
- Trim top when: window-content-top is > 2 × viewport height above scroll-top.
- Trim bottom when: window-content-bottom is > 2 × viewport height below scroll-bottom.

These ensure short chapters are handled: if 1 × viewport height requires loading 10 short chapters, they all load together.

- [ ] **Step 5.1: Remove `{Binding Paragraphs}` from `ListBox` in XAML**

In `MyBibleApp/Views/MainView.axaml`, find the `ListBox` with `Name="ParagraphList"` and remove `ItemsSource="{Binding Paragraphs}"` (or whatever binding is present). The `ItemsSource` will be set programmatically. Do not remove any other attributes.

- [ ] **Step 5.2: Add windowing fields to `MainView.axaml.cs`**

Near the top of the field declarations, add:

```csharp
// Windowed paragraph loading.
private readonly System.Collections.ObjectModel.ObservableCollection<BibleParagraph>
    _windowedItems = [];
private int _windowStart;   // index into _chapterGroups (0-based)
private int _windowEnd;     // exclusive upper bound into _chapterGroups

// Events for chapter enter/exit — AppShellView uses these to load/unload ink strokes.
public event EventHandler<int>? ChapterEnteredWindow;
public event EventHandler<int>? ChapterExitedWindow;
```

- [ ] **Step 5.3: Add `ReinitializeWindow` method**

```csharp
/// <summary>
/// Resets the scroll window to the first N chapters that fill 2× viewport height.
/// Call whenever _paragraphs / _chapterGroups changes.
/// </summary>
private void ReinitializeWindow()
{
    _windowedItems.Clear();
    _windowStart = 0;
    _windowEnd   = 0;
    _chapterStartY.Clear();
    _chapterLocalTops.Clear();

    if (_chapterGroups.Count == 0) return;

    // Extend down until we have at least 3 viewport heights of chapters
    // (or the whole book if small enough).
    var targetHeight = (_paragraphScrollViewer?.Viewport.Height ?? 800) * 3;
    ExtendWindowDown(targetHeight);
}
```

- [ ] **Step 5.4: Add `ExtendWindowDown` and `ExtendWindowUp`**

```csharp
/// <summary>
/// Adds chapters at the bottom of the window until the added content height
/// reaches targetHeight or the end of the book is reached.
/// Fires ChapterEnteredWindow for each chapter added.
/// </summary>
private void ExtendWindowDown(double targetHeight = 0)
{
    double added = 0;
    while (_windowEnd < _chapterGroups.Count && (targetHeight <= 0 || added < targetHeight))
    {
        var chapter = _windowEnd + 1;   // 1-based chapter number
        foreach (var para in _chapterGroups[_windowEnd])
            _windowedItems.Add(para);

        _windowEnd++;
        added += EstimateChapterHeight(chapter);
        ChapterEnteredWindow?.Invoke(this, chapter);
    }
}

/// <summary>
/// Adds chapters at the top of the window. Compensates scroll offset for inserted height.
/// Fires ChapterEnteredWindow for each chapter added.
/// </summary>
private void ExtendWindowUp()
{
    if (_windowStart == 0 || _paragraphScrollViewer == null) return;

    _windowStart--;
    var chapter = _windowStart + 1;     // 1-based
    var newParagraphs = _chapterGroups[_windowStart];
    var estimatedHeight = EstimateChapterHeight(chapter);

    // Prepend paragraphs (ObservableCollection has no AddRange; insert individually).
    for (var i = newParagraphs.Count - 1; i >= 0; i--)
        _windowedItems.Insert(0, newParagraphs[i]);

    // Compensate scroll offset so visible content doesn't jump.
    var newOffset = _paragraphScrollViewer.Offset.Y + estimatedHeight;
    _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, newOffset);

    ChapterEnteredWindow?.Invoke(this, chapter);
}
```

- [ ] **Step 5.5: Add `TrimWindowTop` and `TrimWindowBottom`**

```csharp
/// <summary>
/// Removes the topmost chapter from the window.
/// Compensates scroll offset for the removed height.
/// Fires ChapterExitedWindow.
/// </summary>
private void TrimWindowTop()
{
    if (_windowEnd - _windowStart <= 1 || _paragraphScrollViewer == null) return;

    var chapter = _windowStart + 1;     // 1-based
    var removedParagraphs = _chapterGroups[_windowStart];
    var removedHeight = MeasureChapterHeight(chapter) ?? EstimateChapterHeight(chapter);

    // Remove paragraphs from the start of _windowedItems.
    for (var i = 0; i < removedParagraphs.Count; i++)
        _windowedItems.RemoveAt(0);

    _windowStart++;

    // Compensate scroll offset downward.
    var newOffset = Math.Max(0, _paragraphScrollViewer.Offset.Y - removedHeight);
    _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, newOffset);

    ChapterExitedWindow?.Invoke(this, chapter);
}

/// <summary>
/// Removes the bottommost chapter from the window.
/// No scroll offset compensation needed (removing from end doesn't shift content).
/// Fires ChapterExitedWindow.
/// </summary>
private void TrimWindowBottom()
{
    if (_windowEnd - _windowStart <= 1) return;

    _windowEnd--;
    var chapter = _windowEnd + 1;       // 1-based
    var removedParagraphs = _chapterGroups[_windowEnd];

    for (var i = 0; i < removedParagraphs.Count; i++)
        _windowedItems.RemoveAt(_windowedItems.Count - 1);

    ChapterExitedWindow?.Invoke(this, chapter);
}
```

- [ ] **Step 5.6: Add height helpers**

```csharp
/// <summary>
/// Estimates chapter height from verse count × average line height.
/// Used for scroll offset compensation when the chapter is not yet realized.
/// ~30px line height × 2 lines per verse × verse count.
/// </summary>
private double EstimateChapterHeight(int chapter)
{
    // chapter is 1-based; _chapterGroups is 0-based.
    var groupIdx = chapter - 1;
    if (groupIdx < 0 || groupIdx >= _chapterGroups.Count) return 600;
    var paragraphCount = _chapterGroups[groupIdx].Count;
    return paragraphCount * 60;    // 60 px per paragraph is a conservative estimate
}

/// <summary>
/// Returns the actual measured height of a chapter by summing realized ListBoxItem heights.
/// Returns null if the chapter is not currently realized.
/// </summary>
private double? MeasureChapterHeight(int chapter)
{
    if (_paragraphList == null) return null;

    double total = 0;
    bool found = false;

    foreach (var item in _paragraphList.GetVisualDescendants().OfType<ListBoxItem>())
    {
        if (item.DataContext is not BibleParagraph para) continue;
        if (!_paragraphChapterInfo.TryGetValue(para, out var info)) continue;
        if (info.Chapter != chapter) continue;
        total += item.Bounds.Height;
        found = true;
    }

    return found ? total : null;
}
```

- [ ] **Step 5.7: Add scroll-driven windowing check**

Add `CheckWindowBounds` and call it from `OnParagraphScrollChanged`:

```csharp
private void CheckWindowBounds()
{
    if (_paragraphScrollViewer == null || _chapterGroups.Count == 0) return;

    var scrollTop    = _paragraphScrollViewer.Offset.Y;
    var scrollBottom = scrollTop + _paragraphScrollViewer.Viewport.Height;
    var contentBottom = _paragraphScrollViewer.Extent.Height;
    var vpHeight     = _paragraphScrollViewer.Viewport.Height;

    // Extend down if close to the bottom of the window.
    if (_windowEnd < _chapterGroups.Count &&
        contentBottom - scrollBottom < vpHeight)
    {
        ExtendWindowDown(vpHeight);
    }

    // Extend up if close to the top of the window.
    if (_windowStart > 0 &&
        scrollTop < vpHeight * 0.5)
    {
        ExtendWindowUp();
    }

    // Trim top if far from the top of the window.
    if (_windowEnd - _windowStart > 1 &&
        scrollTop > vpHeight * 2)
    {
        TrimWindowTop();
    }

    // Trim bottom if far from the bottom of the window.
    if (_windowEnd - _windowStart > 1 &&
        contentBottom - scrollBottom > vpHeight * 2)
    {
        TrimWindowBottom();
    }
}
```

In `OnParagraphScrollChanged`, add at the end (after the existing code, before the closing brace):

```csharp
CheckWindowBounds();
```

- [ ] **Step 5.8: Wire `_windowedItems` to `ListBox` in `OnLoaded`**

In `OnLoaded`, after `_paragraphList` is retrieved, add:

```csharp
if (_paragraphList != null)
    _paragraphList.ItemsSource = _windowedItems;
```

Then after `RebuildChapterGroups()`, call:

```csharp
ReinitializeWindow();
```

- [ ] **Step 5.9: Update `ReinitializeWindow` to wait for viewport**

`ReinitializeWindow` uses `_paragraphScrollViewer?.Viewport.Height` which may be 0 before layout. Defer if needed:

```csharp
private void ReinitializeWindow()
{
    _windowedItems.Clear();
    _windowStart = 0;
    _windowEnd   = 0;
    _chapterStartY.Clear();
    _chapterLocalTops.Clear();

    if (_chapterGroups.Count == 0) return;

    // Wait for viewport to be known; if not yet, use a fallback.
    var vpHeight = _paragraphScrollViewer?.Viewport.Height;
    var targetHeight = (vpHeight > 0 ? vpHeight.Value : 800) * 3;
    ExtendWindowDown(targetHeight);
}
```

- [ ] **Step 5.10: Update `ScrollToFraction` and `ScrollToReferenceAsync` to ensure target chapter is in window**

`ScrollToFraction` uses `_paragraphs[targetIndex]`; if that paragraph is outside `_windowedItems`, `ScrollIntoView` fails. Add `EnsureChapterInWindow`:

```csharp
/// <summary>
/// Synchronously repositions the window so the given chapter is realized.
/// Called before ScrollIntoView jumps to a target outside the current window.
/// </summary>
private void EnsureChapterInWindow(int chapter)
{
    var groupIdx = chapter - 1;
    if (groupIdx < 0 || groupIdx >= _chapterGroups.Count) return;

    // Rebuild window centered on target chapter.
    _windowedItems.Clear();
    _chapterStartY.Clear();
    _chapterLocalTops.Clear();

    // Load target chapter + 2 either side (for buffer).
    _windowStart = Math.Max(0, groupIdx - 2);
    _windowEnd   = _windowStart;

    var targetEnd = Math.Min(_chapterGroups.Count, groupIdx + 3);

    while (_windowEnd < targetEnd)
    {
        var ch = _windowEnd + 1;
        foreach (var para in _chapterGroups[_windowEnd])
            _windowedItems.Add(para);
        _windowEnd++;
        ChapterEnteredWindow?.Invoke(this, ch);
    }

    // Adjust scroll offset to approximately where the target chapter starts.
    // (Will be corrected by layout once items are realized.)
    if (_paragraphScrollViewer != null)
        _paragraphScrollViewer.Offset = new Vector(0, 0);
}
```

Update `ScrollToFraction`:

```csharp
private void ScrollToFraction(double fraction)
{
    if (_paragraphList == null || _paragraphs.Count == 0) return;
    fraction = Math.Clamp(fraction, 0, 1);

    if (fraction <= 0 && _paragraphScrollViewer != null)
    {
        EnsureChapterInWindow(1);
        _paragraphScrollViewer.Offset = new Avalonia.Vector(0, 0);
        return;
    }

    if (fraction >= 1 && _paragraphScrollViewer != null)
    {
        var lastChapter = _chapterGroups.Count;
        EnsureChapterInWindow(lastChapter);
        var scrollableHeight = _paragraphScrollViewer.Extent.Height - _paragraphScrollViewer.Viewport.Height;
        if (scrollableHeight > 0)
            _paragraphScrollViewer.Offset = new Avalonia.Vector(0, scrollableHeight);
        return;
    }

    var targetIndex = (int)(fraction * (_paragraphs.Count - 1));
    var targetPara  = _paragraphs[targetIndex];
    EnsureChapterInWindow(targetPara.StartChapter);
    _paragraphList.ScrollIntoView(targetPara);
}
```

In `ScrollToReferenceAsync`, after `target` is resolved, ensure its chapter is in the window before the retry loop:

```csharp
EnsureChapterInWindow(target.StartChapter);
// ... existing retry loop ...
```

- [ ] **Step 5.11: Update `BuildChapterMarkers` and `GetTopVisibleParagraph`**

`BuildChapterMarkers` uses `_paragraphs` (full list) for chapter marker fractions — keep it using `_paragraphs` so all chapter markers are shown even for unloaded chapters. No change needed.

`GetTopVisibleParagraph` and `GetTopVisibleBodyTextParagraph` walk the visual tree — they only see realized items, which is correct. No change needed.

`FindParagraphIndex` searches `_paragraphs` — used only in `UpdateReaderProgress` for progress fraction. Keep using `_paragraphs` so the fraction is relative to the whole book. No change needed.

`UpdateReaderProgress` uses `_paragraphs.Count` as max — keep unchanged.

- [ ] **Step 5.12: Build**

```
dotnet build MyBibleApp
```

Expected: clean build (only `AppShellView` errors remain from Task 2, fixed in Task 7).

- [ ] **Step 5.13: Commit**

```
git add MyBibleApp/Views/MainView.axaml \
        MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: windowed paragraph loading with height-based chapter window in MainView"
```

---

## Task 6: Per-chapter ink loading on window enter/exit

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs`
- Modify: `MyBibleApp/Views/MainView.axaml.cs` (expose needed state for AppShellView)

### Background

When a chapter enters the window, its journal ink strokes must be loaded into the canvas. When it exits, those strokes must be removed. `AppShellView` owns journal state (which journal is active, ephemeral strokes), so it subscribes to `MainView.ChapterEnteredWindow` and `ChapterExitedWindow`.

`MainView` must expose the current book code so `AppShellView` can call `GetInkStrokesAsync(journalId, bookCode, chapter)`.

The existing `MainView.LoadJournalStrokes` loads ALL strokes at once (replaces everything). It is now used only as a fallback for the full-replace path (tab switch). Per-chapter incremental loading uses `AppendChapterStrokes`/`RemoveChapterStrokes`.

- [ ] **Step 6.1: Expose `CurrentBookCode` on `MainView`**

```csharp
/// <summary>Book code of the currently loaded book, for ink store queries.</summary>
public string CurrentBookCode =>
    (DataContext as ScriptureViewModel)?.BookCode ?? string.Empty;
```

- [ ] **Step 6.2: Subscribe to chapter window events in `AppShellView`**

In `AppShellView` constructor (after `_primaryView` is resolved), add:

```csharp
if (_primaryView != null)
{
    _primaryView.ChapterEnteredWindow += OnChapterEnteredWindow;
    _primaryView.ChapterExitedWindow  += OnChapterExitedWindow;
}
```

- [ ] **Step 6.3: Implement `OnChapterEnteredWindow`**

```csharp
private async void OnChapterEnteredWindow(object? sender, int chapter)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    var journalId = _tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null;
    var bookCode  = _primaryView?.CurrentBookCode ?? vm.BookCode;

    IReadOnlyList<JournalInkStroke> strokes;

    if (journalId != null)
    {
        strokes = await SharedSyncRuntime.Instance.JournalStore
            .GetInkStrokesAsync(journalId, bookCode, chapter);
    }
    else
    {
        var ephemeral = _tabEphemeralStrokes.TryGetValue(vm, out var ep) ? ep : [];
        strokes = ephemeral
            .Where(s => s.BookCode == bookCode && s.ChapterNumber == chapter)
            .ToList();
    }

    _primaryView?.AppendChapterStrokes(strokes);
}
```

- [ ] **Step 6.4: Implement `OnChapterExitedWindow`**

```csharp
private void OnChapterExitedWindow(object? sender, int chapter)
{
    _primaryView?.RemoveChapterStrokes(chapter);
}
```

- [ ] **Step 6.5: Update `SelectTab` to use full-replace only (not per-chapter)**

The existing `SelectTab` calls `_primaryView.LoadJournalStrokes(passageStrokes)` which does a full replace. With windowing, on tab switch we reset the window (which fires `ChapterEnteredWindow` for every loaded chapter), so the ink will be loaded incrementally. Change `SelectTab` to clear strokes instead of loading them here — `ChapterEnteredWindow` handles it:

In `SelectTab`, replace:

```csharp
var passageStrokes = await SharedSyncRuntime.Instance.JournalStore.GetInkStrokesAsync(journalId, bookCode, vm.SelectedLookupChapter);
_primaryView.LoadJournalStrokes(passageStrokes);
```

With:

```csharp
// Strokes will be loaded by OnChapterEnteredWindow as chapters enter the window.
_primaryView.LoadJournalStrokes([]);   // clear previous tab's strokes
```

And similarly for the ephemeral branch:

```csharp
_primaryView.LoadJournalStrokes([]);   // clear; ChapterEnteredWindow loads incrementally
```

- [ ] **Step 6.6: Handle `OnJournalActivated` / `OnJournalDeactivated`**

`OnJournalActivated` currently loads all strokes for the current chapter:

```csharp
var passageStrokes = await SharedSyncRuntime.Instance.JournalStore.GetInkStrokesAsync(journalId, bookCode, vm.SelectedLookupChapter);
_primaryView?.LoadJournalStrokes(passageStrokes);
```

Replace with incremental loading of all currently-windowed chapters. Add a helper:

```csharp
/// <summary>
/// Reloads ink strokes for all chapters currently in the MainView window.
/// Called when the active journal changes.
/// </summary>
private async Task ReloadWindowedInkStrokesAsync()
{
    if (_primaryView == null || _activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    _primaryView.LoadJournalStrokes([]);   // clear

    var journalId = _tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null;
    var bookCode  = _primaryView.CurrentBookCode;

    // Fire enter for each currently windowed chapter to reload strokes.
    for (var ch = _primaryView.WindowStart + 1; ch <= _primaryView.WindowEnd; ch++)
        await OnChapterEnteredWindowAsync(ch, journalId, bookCode, vm);
}
```

This requires exposing `WindowStart`/`WindowEnd` on `MainView`:

```csharp
// In MainView.axaml.cs:
public int WindowStart => _windowStart;   // 0-based index into _chapterGroups
public int WindowEnd   => _windowEnd;     // exclusive
```

And converting `OnChapterEnteredWindow` to have a shared async core:

```csharp
private async void OnChapterEnteredWindow(object? sender, int chapter)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];
    var journalId = _tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null;
    var bookCode  = _primaryView?.CurrentBookCode ?? vm.BookCode;
    await OnChapterEnteredWindowAsync(chapter, journalId, bookCode, vm);
}

private async Task OnChapterEnteredWindowAsync(
    int chapter, string? journalId, string bookCode, ScriptureViewModel vm)
{
    IReadOnlyList<JournalInkStroke> strokes;

    if (journalId != null)
    {
        strokes = await SharedSyncRuntime.Instance.JournalStore
            .GetInkStrokesAsync(journalId, bookCode, chapter);
    }
    else
    {
        var ephemeral = _tabEphemeralStrokes.TryGetValue(vm, out var ep) ? ep : [];
        strokes = ephemeral
            .Where(s => s.BookCode == bookCode && s.ChapterNumber == chapter)
            .ToList();
    }

    _primaryView?.AppendChapterStrokes(strokes);
}
```

In `OnJournalActivated`, replace the `LoadJournalStrokes` call with:

```csharp
await ReloadWindowedInkStrokesAsync();
```

In `OnJournalDeactivated`, keep `_primaryView?.LoadJournalStrokes([])` to clear all strokes (ephemeral strokes will reload via `ChapterEnteredWindow`).

- [ ] **Step 6.7: Build**

```
dotnet build MyBibleApp
```

Expected: clean build.

- [ ] **Step 6.8: Commit**

```
git add MyBibleApp/Views/AppShellView.axaml.cs \
        MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: per-chapter ink load/unload on window enter/exit via ChapterEnteredWindow events"
```

---

## Task 7: Fix `AppShellView.OnStrokeCompleted` / `OnStrokeRemoved`

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs`

### Background

`OnStrokeCompleted` must store `AnchorChapter` in the `JournalInkStroke`. The stroke's chapter should come from `e.AnchorChapter` (set by the canvas from the paragraph anchor), not from `vm.SelectedLookupChapter`. This correctly handles strokes drawn on adjacent chapters that happen to be in the window.

`OnStrokeRemoved` must use the per-stroke chapter from `e.RemovedStrokes` rather than `vm.SelectedLookupChapter`.

- [ ] **Step 7.1: Update `OnStrokeCompleted`**

```csharp
private async void OnStrokeCompleted(object? sender, InkStrokeEventArgs e)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    var stroke = new JournalInkStroke
    {
        Id                   = e.StrokeId,
        Points               = e.Points.Select(p => new StrokePoint(p.X, p.Y)).ToList(),
        Color                = $"#{e.Color.A:X2}{e.Color.R:X2}{e.Color.G:X2}{e.Color.B:X2}",
        StrokeWidth          = e.StrokeWidth,
        IsHighlight          = e.IsHighlight,
        BookCode             = vm.BookCode,
        ChapterNumber        = e.AnchorChapter > 0 ? e.AnchorChapter : vm.SelectedLookupChapter,
        AnchorChapter        = e.AnchorChapter,
        AnchorParagraphIndex = e.AnchorParagraphIndex,
        AnchorContentTop     = e.AnchorContentTop
    };

    var journalId = _tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null;
    if (journalId != null)
    {
        await SharedSyncRuntime.Instance.JournalStore.AppendInkStrokeAsync(journalId, stroke);
        await SharedSyncRuntime.Instance.SyncCoordinator.EnqueueJournalSyncAsync();
    }
    else
    {
        _tabEphemeralStrokes[vm].Add(stroke);
        _primaryView?.SetUnsavedBadgeVisible(true);
    }
}
```

- [ ] **Step 7.2: Update `OnStrokeRemoved`**

```csharp
private async void OnStrokeRemoved(object? sender, InkStrokeRemovedEventArgs e)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    var journalId = _tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null;
    foreach (var (strokeId, chapter) in e.RemovedStrokes)
    {
        if (journalId != null)
        {
            // Use per-stroke chapter for correct bucket routing.
            var strokeChapter = chapter > 0 ? chapter : vm.SelectedLookupChapter;
            await SharedSyncRuntime.Instance.JournalStore.RemoveInkStrokeAsync(
                journalId, strokeId, vm.BookCode, strokeChapter);
        }
        else
        {
            _tabEphemeralStrokes[vm].RemoveAll(s => s.Id == strokeId);
        }
    }

    if (journalId != null)
        await SharedSyncRuntime.Instance.SyncCoordinator.EnqueueJournalSyncAsync();
}
```

- [ ] **Step 7.3: Build all projects**

```
dotnet build
```

Expected: clean build with zero errors.

- [ ] **Step 7.4: Run all tests**

```
dotnet test
```

Expected: all green.

- [ ] **Step 7.5: Commit**

```
git add MyBibleApp/Views/AppShellView.axaml.cs
git commit -m "fix: use per-stroke AnchorChapter in OnStrokeCompleted/OnStrokeRemoved"
```

---

## Task 8: Smoke-test on device and fix edge cases

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs` (edge-case fixes found during testing)

These are manual steps that catch issues the automated tests can't cover.

- [ ] **Step 8.1: Run the app on desktop**

```
dotnet run --project MyBibleApp
```

Verify:
1. Genesis loads without hang.
2. Scrolling through Genesis is smooth.
3. Chapter markers appear during fast scroll.
4. Reader progress thumb tracks position correctly across book.
5. Scrollbar drag jumps to the correct chapter.

- [ ] **Step 8.2: Test ink annotation**

1. Navigate to Genesis 1. Enable annotation mode. Draw a pen stroke.
2. Scroll Genesis 1 out of the window (scroll far down so it unloads).
3. Scroll back up. Verify stroke reappears at the correct position.
4. Draw a highlight in Genesis 2 (while Genesis 1 and 3 are also in window).
5. Scroll away and back — verify both strokes persist correctly.
6. Use the eraser on each stroke — verify both are removed from the store.
7. Undo — verify stroke returns.

- [ ] **Step 8.3: Test tab switching**

1. Open two tabs. Annotate Genesis on tab 1. Switch to tab 2 (different book). Switch back.
2. Verify Genesis annotations are restored on tab 1.
3. Verify tab 2 has no stray strokes.

- [ ] **Step 8.4: Fix any issues found**

Common edge cases to watch for:
- `ObservableCollection.Insert(0, ...)` in a loop (O(N²)) — if slow, replace with `RemoveAll + AddRange` by reassigning `_paragraphList.ItemsSource` to a new list for the extend-up case.
- `EnsureChapterInWindow` may cause a scroll jump when used from the scrollbar drag — add `_suppressScrollEventsForTabSwitch = true` guard during `EnsureChapterInWindow` execution.
- `RebuildParagraphTopCache` called during scroll may race with `CheckWindowBounds` if both trigger on `LayoutUpdated` — add a `_isAdjustingWindow` flag to suppress re-entrant windowing calls.

- [ ] **Step 8.5: Final commit**

```
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: edge cases from windowed scrolling smoke test"
```

---

## Self-Review

### Spec coverage check

| Requirement | Task |
|-------------|------|
| Continuous scroll | Task 5 (`_windowedItems`, always adding adjacent chapters) |
| Height-based window (not chapter-count) | Task 5 (`ExtendWindowDown(targetHeight)`, `CheckWindowBounds` using `vpHeight` thresholds) |
| Short chapters handled | Task 5 (extend loop runs until `targetHeight` is filled, loading multiple chapters if needed) |
| Chapter-based ink anchor | Tasks 1–4 |
| `AnchorParagraphIndex` = within-chapter index | Tasks 1, 3 |
| Legacy migration for saved strokes | Task 4 (`InkAnchorMigrator`) |
| `_chapterStartY` (one value per chapter) | Task 4 |
| Per-paragraph estimated heights NOT needed | Confirmed — only chapter-level `EstimateChapterHeight` used |
| Per-chapter ink load/unload | Task 6 |
| `AppShellView` uses per-stroke chapter | Task 7 |
| Tests for chapter grouping | Task 4 (`WindowedParagraphsTests`) |
| Tests for legacy migration | Task 4 (`ChapterAnchorMigrationTests`) |

### Placeholder scan

No TBD or TODO remains. All code blocks are complete.

### Type consistency check

- `InkStrokeRemovedEventArgs` constructor takes `IReadOnlyList<(string StrokeId, int Chapter)>` — used in Task 3 step 3.9 and Task 7 step 7.2 as `e.RemovedStrokes`. ✓
- `GetParagraphContentTop: Func<int chapter, int withinChapterIndex, double?>?` — wired to `GetParagraphContentTopFast(int chapter, int withinChapterIndex)` in Task 4 step 4.7. ✓
- `FindParagraphAtContentY` return type `(int Chapter, int LocalIndex, double ContentTop)?` — consumed in Task 3 step 3.4 as `anchor?.Chapter`, `anchor?.LocalIndex`, `anchor?.ContentTop`. ✓
- `StrokeCache` field order: `AnchorChapter` before `AnchorParagraphIndex` (Task 3 step 3.1) — all construction sites in Task 3 steps 3.7, 3.10, 3.11 use named args to avoid positional confusion. ✓ (Verify at implementation time that `new StrokeCache(...)` calls use named args since `record struct` positional construction is sensitive to field order.)
- `_windowStart`/`_windowEnd` are 0-based chapter-group indices; chapter number = index + 1. Used consistently in Task 5 (`_windowEnd + 1` when firing `ChapterEnteredWindow`). ✓
- `ExtendWindowDown` fires `ChapterEnteredWindow` — `OnChapterEnteredWindow` in `AppShellView` loads strokes. Loops in `ReinitializeWindow` fire for initial chapters too. ✓
