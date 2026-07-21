# Pen Annotation System

## Overview

The annotation system lets users draw pen strokes and highlights directly over Bible text using a stylus (or mouse). Strokes are persisted per-chapter inside the active journal, survive scroll/layout changes via paragraph anchoring, and sync across devices.

Two tool types exist:
- **Pen** — opaque ink, `SrcOver` blend, rendered *below* the text so scripture is always readable over it
- **Highlighter** — semi-transparent wash, `Multiply` blend, rendered *above* the text so it darkens the underlying characters

---

## Visual Layer Architecture

Three elements share `Grid.Row="0"` in `InkAreaGrid`:

```
ZIndex -1  │  PenUnderlay       (InkOverlayCanvas, DrawMode=PenOnly)
           │  ← pen strokes rendered here, text renders over them
ZIndex  0  │  ListBox (ParagraphList)
           │  ← Bible text
ZIndex 10  │  InkOverlay        (InkOverlayCanvas, DrawMode=HighlightOnly)
           │  ← highlight strokes rendered here with Multiply blend
ZIndex 20  │  ReaderProgressTrack (scrollbar)
```

Both canvases are `IsHitTestVisible="False"` — pointer events fall through to the `ListBox` for normal touch scrolling. Pen events are intercepted by `MainView` and routed explicitly to `InkOverlay`.

### Why two canvases?

A single canvas above text with `Multiply` blend worked for highlights but made pen strokes look washed out. A single canvas below text couldn't apply `Multiply` (it needs to darken the text layer above it). The two-canvas split solves both: highlights live above and use `Multiply`; pen strokes live below and use `SrcOver`.

`PenUnderlay` has no stroke store of its own — it sets `DataSource = _inkOverlay` and reads strokes directly from `InkOverlay`'s `_cachedStrokes`. `InkOverlay.RegisterSlave(penUnderlay)` ensures `PenUnderlay.InvalidateVisual()` is called whenever `InkOverlay` redraws.

---

## Coordinate Space

All strokes are stored in **content space**:

```
contentX = viewportX − _textColumnOffsetX
contentY = viewportY + _scrollOffsetY
```

`_textColumnOffsetX` is the left edge of the `ListBox` within `InkAreaGrid` (non-zero when a journal layout narrows the text column and centres it). Strokes use column-relative X so they stay aligned when the window is resized.

At render time the transform is inverted:

```
canvas.Translate(_textColumnOffsetX, −_scrollOffsetY + driftDelta)
```

---

## Paragraph Anchoring (Drift Correction)

Avalonia's `StackPanel` can shift paragraph positions as items load/unload. Each stroke records:

- `AnchorParagraphIndex` — index of the nearest paragraph at draw time
- `AnchorContentTop` — that paragraph's content-space Y at draw time

At render time `GetDriftDelta()` compares the anchor paragraph's *current* Y against the recorded Y and adds the difference to the translation. This keeps strokes visually glued to their paragraph even as the virtualizing panel re-measures.

---

## Stroke Lifecycle

### Input

1. `MainView.OnListBoxPenPressed` fires when `AnnotationToggle` is checked and the pointer type is `Pen`.
2. `_inkOverlay.StartStroke(pos)` converts viewport → content space, records the anchor paragraph, and starts `_activeStroke`.
3. Pointer capture is transferred to `_inkOverlay` so all subsequent `PointerMoved`/`PointerReleased` go directly to it.
4. Each `PointerMoved` calls `ContinueStroke`. Points closer than ~1.4 px to the previous point are dropped (distance filter) to keep point counts manageable.
5. `PointerReleased` calls `EndStroke`, which commits the active stroke to `_cachedStrokes` and fires `StrokeCompleted`.

### Completion

`EndStroke` handles two cases:

| Points | Stored as |
|--------|-----------|
| 1 | `DotCenter` dot; `Points = null`; no `CachedPath` |
| ≥ 2 | `Points` list + `CachedPath` (Catmull-Rom smooth path) |

`StrokeCompleted` fires with the stroke's ID, raw points, colour, width, `IsHighlight`, and anchor data.

### Undo / Redo

`UndoStroke` pops the last entry from `_cachedStrokes` onto `_redoStack` and fires `StrokeRemoved`. `RedoStroke` reverses this and fires `StrokeCompleted`. Both notify the journal store so persistence stays in sync.

### Eraser

`EraseAt` (called on each `PointerMoved` in eraser mode) does:
1. AABB cull against each stroke's `ContentBounds` (expanded by eraser radius).
2. Exact hit test: point–circle for dots, segment–circle for polylines using `DistToSegmentSq`.
3. Removes hits from `_cachedStrokes`, collects their IDs, fires `StrokeRemoved` once.

---

## Rendering Pipeline

### `InkOverlayCanvas.Render(DrawingContext)`

Called by Avalonia on the render thread when `InvalidateVisual()` has been scheduled.

1. Reads strokes from `DataSource ?? this`.
2. Culls to a ±2000 px viewport window around the current scroll offset.
3. Filters by `DrawMode` (`PenOnly` / `HighlightOnly` / `All`).
4. Packages visible strokes + the active stroke (if any) into a `SkiaInkDrawOperation` and passes it to `context.Custom(...)`.

### `SkiaInkDrawOperation.Render(ImmediateDrawingContext)`

Called on the Skia render thread. Issues draw calls using a single reused `SKPaint`:

1. **Highlights** — `BlendMode = SKBlendMode.Multiply`, alpha 128 (50%)
2. **Pen strokes** — `BlendMode = SKBlendMode.SrcOver`, full alpha from stroke colour
3. Each stroke is wrapped in `canvas.Save/Translate/Restore` to apply the scroll + drift transform.

### `DrawStroke`

For dots: `canvas.DrawCircle` (Fill style).  
For polylines:
- If `StrokeCache.CachedPath != null` → `canvas.DrawPath(cachedPath)` — O(1), reuses pre-built geometry.
- Otherwise (active stroke in progress) → builds a `LineTo` path on the fly — only one stroke at a time, acceptable cost.

---

## Performance Design

### Problem

Naively rebuilding `SKPath` from all points of every visible stroke on every render frame produces O(N × P) path construction where N = stroke count and P = average points per stroke. At 100 strokes × 200 pts × 60fps that's ~1.2M point operations/second, causing scroll lag and — because the render thread is busy — OS pointer-event coalescing that reduces captured point density ("resolution loss").

### Cached Paths

`StrokeCache.CachedPath` (an `SKPath?`) is built exactly once:
- at `EndStroke` for user-drawn strokes
- at `LoadJournalStrokes` for loaded strokes
- at `RedoStroke` (re-uses the existing path that came off `_redoStack`)

On every subsequent render frame, `DrawStroke` calls `canvas.DrawPath(cachedPath)` directly — no allocation, no point iteration.

### Catmull-Rom Smoothing

`BuildSmoothPath` converts the raw `IReadOnlyList<Point>` into a C¹-continuous cubic Bézier spline using the Catmull-Rom parameterisation:

```
cp1 = P1 + (P2 − P0) / 6
cp2 = P2 − (P3 − P1) / 6
segment = CubicTo(cp1, cp2, P2)
```

This produces visually smooth strokes even when input points are sparse — important because pointer events are sometimes coalesced under load, leaving large gaps between sampled positions that straight `LineTo` segments would render as jagged angles.

Active strokes use plain `LineTo` during drawing for maximum responsiveness, then get the smooth path baked in at `EndStroke`.

### Distance Filter

`ContinueStroke` skips any point with squared distance < 2.0 from the previous point (~1.4 px threshold). This caps point density on high-DPI displays, keeping `_activeStroke` lists short and reducing the cost of the one active-stroke path rebuild per frame.

### Slave Invalidation

`Redraw()` calls `InvalidateVisual()` on `InkOverlay` **and** on all registered slaves (`PenUnderlay`). This ensures both layers always repaint together without needing duplicate `UpdateScrollOffset` calls in `MainView`.

---

## Persistence

### Journal Store

`IJournalStore` is the persistence boundary. Ink strokes are stored per `(journalId, bookCode, chapter)` bucket.

Key methods used by the annotation system:

| Method | When called |
|--------|-------------|
| `AppendInkStrokeAsync` | `StrokeCompleted` event → `AppShellView.OnStrokeCompleted` |
| `RemoveInkStrokeAsync` | `StrokeRemoved` event → `AppShellView.OnStrokeRemoved` |
| `GetInkStrokesAsync` | When a journal tab is activated; strokes are loaded into the canvas via `LoadJournalStrokes` |
| `SaveAllInkStrokesAsync` | When ephemeral strokes are attached to a newly created journal |

### Ephemeral Strokes

If no journal is active when the user draws, strokes are buffered in `_tabEphemeralStrokes[vm]` (a `List<JournalInkStroke>` per tab). When the user saves to a new journal, `SaveAllInkStrokesAsync` writes all buffered strokes at once.

### Sync

After every `AppendInkStrokeAsync` / `RemoveInkStrokeAsync`, `SyncCoordinator.EnqueueJournalSyncAsync()` is called to propagate changes to other devices.

---

## Tab State Management

`MainView` exposes `CaptureInkState()` / `RestoreInkState(state)` backed by `InkOverlayCanvas.InkState` — an opaque snapshot of the completed `StrokeCache` list. `AppShellView` stores one snapshot per tab in `_tabInkStates` and swaps them on tab activation. This keeps each tab's visual ink state independent without re-loading from the store.

---

## Key Files

| File | Role |
|------|------|
| `MyBibleApp/Controls/InkOverlayCanvas.cs` | All stroke input, storage, rendering, eraser logic |
| `MyBibleApp/Views/MainView.axaml` | XAML placement of `PenUnderlay` (ZIndex −1) and `InkOverlay` (ZIndex 10) |
| `MyBibleApp/Views/MainView.axaml.cs` | Pen event routing, scroll sync, `CaptureInkState`/`RestoreInkState`, `LoadJournalStrokes` |
| `MyBibleApp/Views/AppShellView.axaml.cs` | Connects strokes to persistence and sync; manages tab ink state |
| `MyBibleApp/Services/IJournalStore.cs` | Persistence contract |
| `MyBibleApp/Models/Journal.cs` | `JournalInkStroke`, `StrokePoint` data models |

---

## Performance Notes

### Paragraph position cache (`_paragraphContentTops`)

`GetDriftDelta` is called for every stroke on every render frame. Early implementations called `GetParagraphContentTopByIndex` which walked `_paragraphList.GetVisualDescendants()` — a full visual-tree traversal — per stroke per frame. With 50 strokes this produced ~50 × N_paragraphs element lookups per frame, causing severe frame-rate degradation and OS pointer-event coalescing ("resolution loss").

Fix: `MainView` maintains `double[] _paragraphContentTops`, rebuilt in `OnParagraphListLayoutUpdated` (one visual-tree walk per layout change). `GetParagraphContentTopFast` is an O(1) array lookup. Also fixes a pre-existing threading issue — the old callback read Avalonia visual tree objects from the render thread.

### Smart invalidation during drawing

`Redraw()` invalidates both `InkOverlay` and `PenUnderlay`. During active pen drawing, `InkOverlay` (DrawMode=HighlightOnly) has nothing new to show; invalidating it is pure waste at 120Hz. `RedrawActiveStrokeLayer()` invalidates only the layer whose stroke type is currently being drawn (pen → PenUnderlay only; highlight → InkOverlay only). `ContinueStroke` uses this instead of `Redraw()`.

### SKPath caching + Catmull-Rom smoothing

Each completed stroke carries a pre-built `SKPath` (Catmull-Rom cubic Bézier) in `StrokeCache.CachedPath`. `DrawStroke` calls `canvas.DrawPath(cachedPath)` — O(1) per frame regardless of point count. The active stroke rebuilds a `LineTo` path each frame (one stroke, bounded cost). Point density is capped by a 1.4 px distance filter in `ContinueStroke`.

## Layout Engine Versioning

Stroke anchors (`AnchorParagraphIndex` + `AnchorContentTop`) are only valid relative to the layout engine that produced the paragraph positions at draw time. If the rendering layout changes — font metrics, column width, paragraph spacing, USX parsing, scroll virtualisation — the stored `AnchorContentTop` values become stale and strokes will appear shifted.

**Rule:** any change that affects paragraph Y-positions for the same bible content must increment `JournalLayout.CurrentVersion` (defined in `MyBibleApp/Models/Journal.cs`). New and resaved journals write `LayoutEngineVersion = JournalLayout.CurrentVersion`. Legacy journals (version 0) predate this field.

On load, compare `journal.Layout.LayoutEngineVersion` to `JournalLayout.CurrentVersion`. A mismatch means anchor drift correction may be inaccurate; surface a warning or apply a migration if feasible. Do not silently discard strokes.

---

## Known Constraints

- **Thread safety of `_cachedStrokes`**: the render thread reads `_cachedStrokes` (via `DataSource`) while the UI thread may be appending to it. In practice this is safe because Avalonia schedules renders after the current UI frame, but it is not formally synchronized.
- **`SKPath` disposal**: `CachedPath` objects on removed strokes (undo, erase) are not explicitly disposed; the GC finalizer releases the native Skia memory. For normal annotation volumes this is acceptable.
- **Single active stroke path**: the active stroke rebuilds its `LineTo` path from scratch each frame. This is O(P) for the current stroke only; cost is bounded and not visible in practice.
- **Eraser is whole-stroke only**: hitting any segment of a polyline removes the entire stroke. Sub-stroke splitting is not implemented.
