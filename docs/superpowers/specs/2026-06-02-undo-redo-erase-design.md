# Undo/Redo for Erased Strokes

**Date:** 2026-06-02  
**Scope:** `MyBibleApp/Controls/InkOverlayCanvas.cs` only

## Problem

Erased strokes are permanently lost. `EraseAt` removes strokes from `_cachedStrokes` and clears `_redoStack` without pushing anything recoverable. `UndoStroke` only knows about drawn strokes (it removes the last item from `_cachedStrokes`). Interleaved draws and erases cannot be undone in correct order.

## Design

### Data model

Replace `_redoStack: Stack<StrokeCache>` with two typed stacks:

```csharp
private readonly Stack<(StrokeCache Stroke, bool WasErased)> _undoHistory = new();
private readonly Stack<(StrokeCache Stroke, bool WasErased)> _redoHistory = new();
```

- `WasErased = false` — completed draw stroke; undo removes it from canvas
- `WasErased = true` — erased stroke; undo adds it back to canvas

Single stack ordering guarantees undo always reverses the most recent action regardless of type. All other state (`_cachedStrokes`, `_activeStroke`, etc.) unchanged.

### Granularity

Individual stroke granularity. Each erased stroke is its own undo entry. One undo step per stroke removed, regardless of whether it was drawn or erased.

### Method changes

**`EndStroke` (draw path):**
- Push `(newStroke, false)` to `_undoHistory`
- Clear `_redoHistory`
- Remove existing `_redoStack.Clear()` call

**`EraseAt`:**
- For each stroke removed by hit-test: push `(removedStroke, true)` to `_undoHistory`, clear `_redoHistory`
- Remove existing `_redoStack.Clear()` call
- `StrokeRemoved` event fires unchanged (per-stroke, immediately)

**`UndoStroke`:**
- Pop from `_undoHistory`, push to `_redoHistory`
- `WasErased = false`: remove stroke from `_cachedStrokes` by StrokeId, fire `StrokeRemoved`
- `WasErased = true`: add stroke back to `_cachedStrokes`, fire `StrokeCompleted`

**`RedoStroke`:**
- Pop from `_redoHistory`, push to `_undoHistory`
- `WasErased = false`: add stroke back to `_cachedStrokes`, fire `StrokeCompleted`
- `WasErased = true`: remove stroke from `_cachedStrokes` by StrokeId, fire `StrokeRemoved`

**`RestoreState` / `ClearStrokes` / `LoadJournalStrokes`:**
- Clear both `_undoHistory` and `_redoHistory` (replace `_redoStack.Clear()`)

**`AppendChapterStrokes`:**
- No change — does not touch either history stack (windowed loading bypasses user action history)

### Event contract

`StrokeCompleted` fires when a stroke becomes visible (draw or undo-of-erase). `StrokeRemoved` fires when a stroke is removed (erase, undo-of-draw, or redo-of-erase). These contracts are unchanged; callers in `AppShellView` route to journal/ephemeral store correctly.

### Edge cases

**Chapter trimmed from window during undo-of-draw:**  
`RemoveAll(x => x.StrokeId == id)` removes 0 items silently. `StrokeRemoved` still fires → journal removes the stroke. Correct.

**Chapter trimmed during undo-of-erase:**  
Stroke re-added to `_cachedStrokes` but renders invisible (no layout entry). `StrokeCompleted` fires → journal saves it. When chapter re-enters, `AppendChapterStrokes` may create a duplicate in `_cachedStrokes` — pre-existing issue with draw-undo, out of scope.

**UI buttons:**  
No changes needed. Buttons call `UndoStroke()`/`RedoStroke()` with no enable-state binding; both methods guard internally with count checks on the new stacks.

## Files changed

| File | Change |
|------|--------|
| `MyBibleApp/Controls/InkOverlayCanvas.cs` | Replace `_redoStack` with `_undoHistory`/`_redoHistory`; update `EndStroke`, `EraseAt`, `UndoStroke`, `RedoStroke`, `RestoreState`, `ClearStrokes`, `LoadJournalStrokes` |
