# Mobile Scrollbar Accidental Trigger Fix

**Date:** 2026-06-13  
**Status:** Approved

## Problem

Custom reader progress scrollbar (`ReaderProgressTrack`, 12px wide, right edge) fires `ScrollToFraction` on any touch — no threshold. Two accidental trigger scenarios on mobile:

- A) Finger drifts right during content scroll, lands on track → scroll position jumps
- B) Tapping near right edge (e.g. text selection) activates scrollbar

## Solution

Combine two independent guards, both mobile-only:

### Guard 1 — Invisible = non-hit-testable

Track can only receive pointer events when visible.

- **Init (mobile):** `IsHitTestVisible = false`
- **`ShowScrollbarBriefly()`:** set `IsHitTestVisible = true` when making visible
- **Hide (timer expiry):** set `IsHitTestVisible = false` when fading out
- **During drag:** keep `IsHitTestVisible = true` (scrollbar stays visible while dragging)
- **Desktop:** no change — always hit-testable

Tap-to-reveal still works via `OnListBoxTapped` (on the ListBox, not the track).

### Guard 2 — Drag distance threshold

`OnProgressTrackPointerPressed` no longer immediately jumps scroll. Dragging activates only after ≥8px movement.

**New fields:**
```csharp
private double _dragStartY;
private bool _isPressedOnTrack;  // pressed but may not have crossed drag threshold yet
```

**`OnProgressTrackPointerPressed` changes:**
- Capture pointer
- Set `_isPressedOnTrack = true`
- Record `_dragStartY = e.GetPosition(_readerProgressTrack).Y`
- Cancel `_scrollbarHideCts` (keep visible)
- Do NOT set `_isDraggingProgressBar = true` yet
- Do NOT call `ScrollToFraction` yet
- Do NOT call `BuildChapterMarkers` yet

**`OnProgressTrackPointerMoved` changes:**
- Guard: `if (!_isPressedOnTrack || _readerProgressTrack == null) return`
- If `!_isDraggingProgressBar`: check `Math.Abs(currentY - _dragStartY) >= 8.0`
  - If threshold not met: return early
  - If threshold met: set `_isDraggingProgressBar = true`, call `BuildChapterMarkers()`
- Existing drag logic unchanged after activation

**`OnProgressTrackPointerReleased` changes:**
- Set `_isPressedOnTrack = false`

- If `_isDraggingProgressBar` was never set (tap without drag):
  - Release pointer capture
  - Call `ShowScrollbarBriefly()` (restart hide timer)
  - Return — no scroll jump
- Otherwise: existing release logic unchanged

**Threshold:** 8px — imperceptible on intentional drag, filters accidental tap.

## Affected Files

- `MyBibleApp/Views/MainView.axaml.cs` — pointer handlers + `ShowScrollbarBriefly()` + init
- `MyBibleApp/Views/MainView.axaml` — no changes needed

## Out of Scope

- Desktop behavior (unchanged)
- Scrollbar visibility duration / opacity (unchanged)
- Thumb size / appearance (unchanged)
