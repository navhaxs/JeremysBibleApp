# Undo/Redo for Erased Strokes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend undo/redo so erased strokes can be individually restored or re-erased, interleaved correctly with drawn strokes.

**Architecture:** Replace `_redoStack: Stack<StrokeCache>` in `InkOverlayCanvas` with a unified `_undoHistory` + `_redoHistory`, each holding `(StrokeCache Stroke, bool WasErased)` tuples. `WasErased = true` entries restore the stroke on undo; `WasErased = false` entries remove it. `EraseAt` pushes each removed stroke individually. No batching — one undo step per stroke.

**Tech Stack:** C# 10, Avalonia, SkiaSharp. Tests: xUnit in `MyBibleApp.Journal.Tests` (already references `MyBibleApp.csproj`). Note: `InkOverlayCanvas` inherits from Avalonia `Control`, so unit tests verify event output via a thin test harness rather than rendering.

---

## Files

| Action | Path |
|--------|------|
| Modify | `MyBibleApp/Controls/InkOverlayCanvas.cs` |
| Create | `MyBibleApp.Journal.Tests/Unit/InkUndoHistoryTests.cs` |

---

### Task 1: Replace `_redoStack` declaration and clear all old references

**Files:**
- Modify: `MyBibleApp/Controls/InkOverlayCanvas.cs:151`

- [ ] **Step 1: Replace the field declaration**

Find line 151:
```csharp
private readonly Stack<StrokeCache> _redoStack = new();
```
Replace with:
```csharp
private readonly Stack<(StrokeCache Stroke, bool WasErased)> _undoHistory = new();
private readonly Stack<(StrokeCache Stroke, bool WasErased)> _redoHistory = new();
```

- [ ] **Step 2: Fix all `_redoStack.Clear()` calls**

There are 5 occurrences. Replace ALL of them:

In `EndStroke` (draw path, ~line 259):
```csharp
// Remove: _redoStack.Clear();
// This line is removed here — push happens in Task 2
```
_(Leave this site blank for now — Task 2 adds the push + clear together.)_

In `RestoreState` (~line 333):
```csharp
// Remove: _redoStack.Clear();
_undoHistory.Clear();
_redoHistory.Clear();
```

In `ClearStrokes` (~line 344):
```csharp
// Remove: _redoStack.Clear();
_undoHistory.Clear();
_redoHistory.Clear();
```

In `LoadJournalStrokes` (~line 353):
```csharp
// Remove: _redoStack.Clear();
_undoHistory.Clear();
_redoHistory.Clear();
```

In `EraseAt` (~line 563):
```csharp
// Remove: _redoStack.Clear();
// This site is handled in Task 3.
```

- [ ] **Step 3: Build to confirm old references gone**

```
dotnet build MyBibleApp/MyBibleApp.csproj --no-restore 2>&1 | tail -5
```
Expected: errors only on `_redoStack` in `UndoStroke` and `RedoStroke` (2 sites not yet changed). No other errors.

---

### Task 2: Update `EndStroke` to push draw entry

**Files:**
- Modify: `MyBibleApp/Controls/InkOverlayCanvas.cs` — `EndStroke` method (~line 254)

- [ ] **Step 1: Find the single-dot stroke path in EndStroke**

Locate this block (~line 263):
```csharp
_redoStack.Clear();
var id = Guid.NewGuid().ToString();
if (_activeStroke.Count == 1)
{
    var p = _activeStroke[0];
    _cachedStrokes.Add(new StrokeCache(
        p,
        new Rect(p.X - 2, p.Y - 2, 4, 4),
        _activeStrokeColor, _activeStrokeWidth, _activeIsHighlight, null,
        _activeAnchorChapter, _activeAnchorLocalIndex, _activeAnchorContentTop, id));
    StrokeCompleted?.Invoke(this, new InkStrokeEventArgs { ... });
```

- [ ] **Step 2: Replace `_redoStack.Clear()` and push draw entry for single-dot**

```csharp
_redoHistory.Clear();
var id = Guid.NewGuid().ToString();
if (_activeStroke.Count == 1)
{
    var p = _activeStroke[0];
    var dotStroke = new StrokeCache(
        p,
        new Rect(p.X - 2, p.Y - 2, 4, 4),
        _activeStrokeColor, _activeStrokeWidth, _activeIsHighlight, null,
        _activeAnchorChapter, _activeAnchorLocalIndex, _activeAnchorContentTop, id);
    _cachedStrokes.Add(dotStroke);
    _undoHistory.Push((dotStroke, false));
    StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
    {
        StrokeId             = id,
        Points               = [],
        Color                = _activeStrokeColor,
        StrokeWidth          = _activeStrokeWidth,
        IsHighlight          = _activeIsHighlight,
        AnchorChapter        = _activeAnchorChapter,
        AnchorParagraphIndex = _activeAnchorLocalIndex,
        AnchorContentTop     = _activeAnchorContentTop
    });
}
```

- [ ] **Step 3: Push draw entry for multi-point stroke**

Locate the `else` branch (multi-point, ~line 283):
```csharp
else
{
    var pts = _activeStroke.AsReadOnly();
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
    StrokeCompleted?.Invoke(this, new InkStrokeEventArgs { ... });
}
```

Replace with:
```csharp
else
{
    var pts = _activeStroke.AsReadOnly();
    var multiStroke = new StrokeCache(
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
        CachedPath: BuildSmoothPath(pts));
    _cachedStrokes.Add(multiStroke);
    _undoHistory.Push((multiStroke, false));
    StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
    {
        StrokeId             = id,
        Points               = pts,
        Color                = _activeStrokeColor,
        StrokeWidth          = _activeStrokeWidth,
        IsHighlight          = _activeIsHighlight,
        AnchorChapter        = _activeAnchorChapter,
        AnchorParagraphIndex = _activeAnchorLocalIndex,
        AnchorContentTop     = _activeAnchorContentTop
    });
}
```

- [ ] **Step 4: Build to check**

```
dotnet build MyBibleApp/MyBibleApp.csproj --no-restore 2>&1 | tail -5
```
Expected: same remaining errors (UndoStroke/RedoStroke still reference old `_redoStack`).

---

### Task 3: Update `EraseAt` to push erase entries

**Files:**
- Modify: `MyBibleApp/Controls/InkOverlayCanvas.cs` — `EraseAt` method (~line 506)

- [ ] **Step 1: Capture `StrokeCache` before removal in hit-test loop**

In the single-dot hit block (~line 531):
```csharp
if (dx * dx + dy * dy <= radiusSq)
{
    if (!string.IsNullOrEmpty(s.StrokeId))
        (removedStrokes ??= []).Add((s.StrokeId, s.AnchorChapter));
    _cachedStrokes.RemoveAt(i);
}
```
Replace with:
```csharp
if (dx * dx + dy * dy <= radiusSq)
{
    if (!string.IsNullOrEmpty(s.StrokeId))
        (removedStrokes ??= []).Add((s.StrokeId, s.AnchorChapter));
    _undoHistory.Push((s, true));
    _cachedStrokes.RemoveAt(i);
}
```

In the multi-point hit block (~line 553):
```csharp
if (hit)
{
    if (!string.IsNullOrEmpty(s.StrokeId))
        (removedStrokes ??= []).Add((s.StrokeId, s.AnchorChapter));
    _cachedStrokes.RemoveAt(i);
}
```
Replace with:
```csharp
if (hit)
{
    if (!string.IsNullOrEmpty(s.StrokeId))
        (removedStrokes ??= []).Add((s.StrokeId, s.AnchorChapter));
    _undoHistory.Push((s, true));
    _cachedStrokes.RemoveAt(i);
}
```

- [ ] **Step 2: Replace tail of `EraseAt` — remove `_redoStack.Clear()`**

Find (~line 561):
```csharp
if (removedStrokes != null)
{
    _redoStack.Clear();
    Redraw();
    StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(removedStrokes));
}
```
Replace with:
```csharp
if (removedStrokes != null)
{
    _redoHistory.Clear();
    Redraw();
    StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(removedStrokes));
}
```

- [ ] **Step 3: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj --no-restore 2>&1 | tail -5
```
Expected: only UndoStroke/RedoStroke errors remain.

---

### Task 4: Rewrite `UndoStroke` and `RedoStroke`

**Files:**
- Modify: `MyBibleApp/Controls/InkOverlayCanvas.cs` — `UndoStroke` (~line 430), `RedoStroke` (~line 443)

- [ ] **Step 1: Replace `UndoStroke`**

```csharp
/// <summary>Reverses the most recent draw or erase action.</summary>
public void UndoStroke()
{
    if (_undoHistory.Count == 0) return;
    var (stroke, wasErased) = _undoHistory.Pop();
    _redoHistory.Push((stroke, wasErased));

    if (wasErased)
    {
        // Stroke was erased — restore it.
        _cachedStrokes.Add(stroke);
        Redraw();
        if (!string.IsNullOrEmpty(stroke.StrokeId))
        {
            var pts = stroke.Points ?? (IReadOnlyList<Point>)[];
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
        }
    }
    else
    {
        // Stroke was drawn — remove it.
        var idx = _cachedStrokes.FindIndex(x => x.StrokeId == stroke.StrokeId);
        if (idx >= 0) _cachedStrokes.RemoveAt(idx);
        Redraw();
        if (!string.IsNullOrEmpty(stroke.StrokeId))
            StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(
                [(stroke.StrokeId, stroke.AnchorChapter)]));
    }
}
```

- [ ] **Step 2: Replace `RedoStroke`**

```csharp
/// <summary>Re-applies the most recently undone draw or erase action.</summary>
public void RedoStroke()
{
    if (_redoHistory.Count == 0) return;
    var (stroke, wasErased) = _redoHistory.Pop();
    _undoHistory.Push((stroke, wasErased));

    if (wasErased)
    {
        // Originally erased — re-erase it.
        var idx = _cachedStrokes.FindIndex(x => x.StrokeId == stroke.StrokeId);
        if (idx >= 0) _cachedStrokes.RemoveAt(idx);
        Redraw();
        if (!string.IsNullOrEmpty(stroke.StrokeId))
            StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(
                [(stroke.StrokeId, stroke.AnchorChapter)]));
    }
    else
    {
        // Originally drawn — re-add it.
        _cachedStrokes.Add(stroke);
        Redraw();
        if (!string.IsNullOrEmpty(stroke.StrokeId))
        {
            var pts = stroke.Points ?? (IReadOnlyList<Point>)[];
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
        }
    }
}
```

- [ ] **Step 3: Build — must be clean**

```
dotnet build MyBibleApp/MyBibleApp.csproj --no-restore 2>&1 | tail -5
```
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```
git add MyBibleApp/Controls/InkOverlayCanvas.cs
git commit -m "feat: extend undo/redo to include erased strokes"
```

---

### Task 5: Write unit tests

**Files:**
- Create: `MyBibleApp.Journal.Tests/Unit/InkUndoHistoryTests.cs`

Note: `InkOverlayCanvas` inherits from Avalonia `Control` and cannot be instantiated in a plain xUnit context (it calls `InvalidateVisual()` on every action). Tests verify behavior through the public events `StrokeCompleted` and `StrokeRemoved`, using a minimal Avalonia headless setup.

- [ ] **Step 1: Add `Avalonia.Headless.XUnit` package to the test project**

```
dotnet add MyBibleApp.Journal.Tests package Avalonia.Headless.XUnit
```

- [ ] **Step 2: Add AppBuilder bootstrap for headless Avalonia**

Create `MyBibleApp.Journal.Tests/AvaloniaTestApp.cs`:
```csharp
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(MyBibleApp.Journal.Tests.AvaloniaTestApp))]

namespace MyBibleApp.Journal.Tests;

public class AvaloniaTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Avalonia.Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
```

- [ ] **Step 3: Write failing tests**

Create `MyBibleApp.Journal.Tests/Unit/InkUndoHistoryTests.cs`:
```csharp
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using MyBibleApp.Controls;
using MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class InkUndoHistoryTests
{
    private static InkOverlayCanvas MakeCanvas()
    {
        var c = new InkOverlayCanvas { AllowMouseInput = true };
        return c;
    }

    private static void DrawStroke(InkOverlayCanvas c, double x, double y)
    {
        c.StartStroke(new Avalonia.Point(x, y));
        c.ContinueStroke(new Avalonia.Point(x + 10, y + 10));
        c.EndStroke();
    }

    private static void EraseAt(InkOverlayCanvas c, double x, double y)
    {
        c.IsEraserMode = true;
        c.StartStroke(new Avalonia.Point(x, y));
        c.IsEraserMode = false;
    }

    [AvaloniaFact]
    public void UndoStroke_AfterErase_FiresStrokeCompleted()
    {
        var canvas = MakeCanvas();
        InkStrokeEventArgs? restored = null;
        canvas.StrokeCompleted += (_, e) => restored = e;

        DrawStroke(canvas, 100, 100);
        var drawnId = restored?.StrokeId;
        restored = null;

        EraseAt(canvas, 100, 100);
        canvas.UndoStroke();

        Assert.NotNull(restored);
        Assert.Equal(drawnId, restored!.StrokeId);
    }

    [AvaloniaFact]
    public void UndoStroke_AfterErase_ThenRedoStroke_FiresStrokeRemoved()
    {
        var canvas = MakeCanvas();
        InkStrokeEventArgs? completed = null;
        InkStrokeRemovedEventArgs? removed = null;
        canvas.StrokeCompleted += (_, e) => completed = e;
        canvas.StrokeRemoved += (_, e) => removed = e;

        DrawStroke(canvas, 100, 100);
        var drawnId = completed?.StrokeId;

        EraseAt(canvas, 100, 100);
        canvas.UndoStroke();  // restore
        removed = null;

        canvas.RedoStroke();  // re-erase
        Assert.NotNull(removed);
        Assert.Contains(removed!.RemovedStrokes, s => s.StrokeId == drawnId);
    }

    [AvaloniaFact]
    public void UndoStroke_InterleavedDrawAndErase_UndoesInReverseOrder()
    {
        var canvas = MakeCanvas();
        var completed = new List<string>();
        var removed = new List<string>();
        canvas.StrokeCompleted += (_, e) => completed.Add(e.StrokeId);
        canvas.StrokeRemoved += (_, e) => removed.AddRange(e.RemovedStrokes.Select(s => s.StrokeId));

        // Draw A at (100, 100)
        DrawStroke(canvas, 100, 100);
        var idA = completed.Last();

        // Draw B at (300, 300) — far away, won't be erased
        DrawStroke(canvas, 300, 300);
        var idB = completed.Last();

        // Erase A
        EraseAt(canvas, 100, 100);

        // Undo erase — A restored
        canvas.UndoStroke();
        Assert.Equal(idA, completed.Last());

        // Undo draw B — B removed
        canvas.UndoStroke();
        Assert.Equal(idB, removed.Last());

        // Undo draw A — A removed
        canvas.UndoStroke();
        Assert.Equal(idA, removed.Last());
    }

    [AvaloniaFact]
    public void Draw_ClearsRedoHistory()
    {
        var canvas = MakeCanvas();
        var completed = new List<string>();
        canvas.StrokeCompleted += (_, e) => completed.Add(e.StrokeId);

        DrawStroke(canvas, 100, 100);
        canvas.UndoStroke();
        DrawStroke(canvas, 200, 200); // should clear redo

        // Redo should be a no-op now
        canvas.RedoStroke();
        Assert.Single(completed); // only the second draw is on canvas
    }
}
```

- [ ] **Step 4: Run tests to verify they fail (feature not yet fully wired)**

```
dotnet test MyBibleApp.Journal.Tests --filter "InkUndoHistoryTests" 2>&1 | tail -15
```
Expected: tests fail (the new undo/redo behavior is being validated).

Actually at this point the implementation is done (Tasks 1–4), so run all tests:

```
dotnet test MyBibleApp.Journal.Tests --filter "InkUndoHistoryTests" -v 2>&1 | tail -20
```
Expected: all 4 tests pass.

- [ ] **Step 5: Run full test suite**

```
dotnet test MyBibleApp.Journal.Tests 2>&1 | tail -10
```
Expected: all existing tests still pass, 4 new tests pass.

- [ ] **Step 6: Commit**

```
git add MyBibleApp.Journal.Tests/Unit/InkUndoHistoryTests.cs MyBibleApp.Journal.Tests/AvaloniaTestApp.cs MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj
git commit -m "test: add undo/redo erase stroke unit tests"
```

---

### Task 6: Manual verification

- [ ] **Run the app and verify these scenarios:**

  1. Draw a stroke → Erase it → Undo → stroke reappears ✓
  2. Draw a stroke → Erase it → Undo → Redo → stroke disappears again ✓
  3. Draw A → Draw B → Erase A → Undo (erase, A back) → Undo (draw B, B gone) → Undo (draw A, A gone) ✓
  4. Undo/Redo buttons are no-ops when history is empty (no crash) ✓
  5. Drawing a new stroke after undo clears redo (Redo button does nothing after drawing) ✓

```
dotnet run --project MyBibleApp.Desktop
```
