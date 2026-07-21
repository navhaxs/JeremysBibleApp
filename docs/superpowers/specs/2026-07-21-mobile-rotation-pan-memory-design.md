# Mobile Rotation: Per-Orientation Journal Pan Memory — Design Spec

**Date:** 2026-07-21

## Problem

On mobile, journal ink mode uses `ContentHScrollContainer` (`MyBibleApp/Views/MainView.axaml.cs`) to horizontally pan the text/ink column when the viewport is narrower than `TextColumnWidthDip` (see [2026-06-06-journal-horizontal-scroll-design.md](2026-06-06-journal-horizontal-scroll-design.md)). When the device rotates, the viewport width changes and the pan offset (`_contentHScrollContainer.Offset.X`) is clamped/lost, so returning to a previous orientation does not restore the horizontal alignment the user had there.

Desired: rotating vertical → horizontal → vertical → horizontal repeatedly should show the same horizontal alignment each time you're back in a given orientation.

## Scope

- Mobile only (`!PlatformHelper.IsDesktop`). Desktop window resizing is untouched — no orientation tracking runs there.
- Journal ink horizontal pan only (`ContentHScrollContainer` / `_contentHScrollContainer.Offset.X`). Bible Reading grid (`BooksGrid`) h-scroll is out of scope.
- One global pan value per orientation for the whole app (not per journal/chapter).
- Persisted to disk — survives app relaunch.
- Restore uses raw pixel offset, clamped to the new orientation's max scroll range if it no longer fits (`Math.Clamp(stored, 0, maxX)`), matching the existing clamp pattern already used at `MainView.axaml.cs:2614-2616` and `2750-2751`.

## Architecture

### New fields (`MainView.axaml.cs`)

```csharp
private bool? _lastOrientationIsPortrait;   // null = not yet determined
private double _portraitPanX;
private double _landscapePanX;
```

These are the in-memory cache of the persisted values, kept in sync on every pan and loaded once at startup.

### New persistence type: `MyBibleApp/Services/UiPreferencesStore.cs`

A small JSON-file-backed store, following `JournalStore`'s existing conventions (`Environment.SpecialFolder.ApplicationData` base, atomic write via temp-file-then-move — see `JournalStore.cs:640-652`).

- Path: `%AppData%/MyBibleApp/ui-prefs.json`
- Shape:
  ```json
  { "portraitJournalPanX": 0.0, "landscapeJournalPanX": 0.0 }
  ```
- API:
  ```csharp
  public sealed class UiPreferencesStore
  {
      public Task<(double portraitX, double landscapeX)> LoadJournalPanAsync();
      public Task SaveJournalPanAsync(double portraitX, double landscapeX);
  }
  ```
- `LoadJournalPanAsync` is best-effort: missing file or parse failure → `(0, 0)`, no exception surfaced.
- `SaveJournalPanAsync` writes both values together (single small file, no need to split); called fire-and-forget from the UI thread, matching the low-stakes, low-frequency nature of the data (errors are swallowed/logged, never block panning).

## Data Flow

1. **Startup**: `MainView` calls `UiPreferencesStore.LoadJournalPanAsync()` once (e.g. during existing init/`OnAttachedToVisualTree` path) and populates `_portraitPanX` / `_landscapePanX`.

2. **Orientation detection**: hook the `MainView` root `SizeChanged` (gated `!PlatformHelper.IsDesktop`). Derive orientation from bounds: `isPortrait = newSize.Height >= newSize.Width`. Compare against `_lastOrientationIsPortrait`:
   - First call: just record `_lastOrientationIsPortrait`, no restore (nothing stored yet meaningfully differs from current state).
   - On a change: mark a pending restore (mirrors the existing `_journalHScrollNeedsReset` deferred-apply pattern — `Offset` can't be set correctly until `Extent` is valid post-layout).

3. **Live capture on pan**: in the two existing pan-writing sites —
   - `OnMarginTouchMoved` touch-drag (`MainView.axaml.cs:2612-2616`)
   - `OnHorizontalWheelChanged` (`MainView.axaml.cs:2748-2752`)

   immediately after `_contentHScrollContainer.Offset = new Vector(newX, ...)`, also write `newX` into `_portraitPanX` or `_landscapePanX` (whichever matches `_lastOrientationIsPortrait`) and call `UiPreferencesStore.SaveJournalPanAsync(_portraitPanX, _landscapePanX)`.

4. **Restore on rotation**: in the same deferred-apply spot used today for `_journalHomePanX` (the `_inkAreaGrid.SizeChanged` handler, `MainView.axaml.cs:403-417`, guarded on `_contentHScrollContainer.Extent.Width > _contentHScrollContainer.Viewport.Width`), if a pending restore is flagged:
   ```csharp
   var maxX = Math.Max(0, _contentHScrollContainer.Extent.Width - _contentHScrollContainer.Viewport.Width);
   var target = _lastOrientationIsPortrait == true ? _portraitPanX : _landscapePanX;
   _contentHScrollContainer.Offset = new Vector(Math.Clamp(target, 0, maxX), 0);
   ```
   then clear the pending-restore flag.

5. Restore only runs when journal mode is active and h-scroll applies (`_journalHomePanX > 0`, same bail-out condition already used by `UpdateJournalInkAreaGridWidth`, `MainView.axaml.cs:2862`). If journal is inactive, orientation changes are tracked (so `_lastOrientationIsPortrait` stays correct) but no offset restore happens.

## Edge Cases

- **First-ever rotation in a fresh install**: both stored values are `0`, so restore is a no-op; existing `_journalHomePanX` default behavior is unchanged.
- **Journal turned off mid-rotation**: no restore attempted (nothing to align); orientation tracking continues silently.
- **Extent shrinks between orientations** (e.g. stored landscape pan doesn't fit portrait's narrower range): clamped down via `Math.Clamp`.
- **Desktop resize**: orientation tracking code path never runs (`PlatformHelper.IsDesktop` gate at the `SizeChanged` hook), so no behavior change on desktop.

## Files Changed

- `MyBibleApp/Services/UiPreferencesStore.cs` — new file, load/save JSON prefs.
- `MyBibleApp/Views/MainView.axaml.cs` — new fields, root `SizeChanged` orientation hook, live-capture writes in the two pan sites, restore logic in the existing `_inkAreaGrid.SizeChanged` handler.

## Out of Scope

- Bible Reading grid (`BooksGrid`) horizontal scroll memory.
- Per-journal/per-chapter scoping.
- Any change to vertical scroll position handling (already separately windowed — see [[project-windowed-scroll]] memory).
