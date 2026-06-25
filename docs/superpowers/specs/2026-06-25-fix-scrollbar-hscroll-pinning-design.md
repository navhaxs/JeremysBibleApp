# Fix Scrollbar H-Scroll Pinning — Design Spec

**Date:** 2026-06-25
**Status:** Approved

## Problem

`ReaderProgressTrack` (custom vertical scrollbar, 12px wide) and `ChapterMarkersCanvas` (chapter label strip, 52px wide) live inside `InkAreaGrid`, which is wrapped by `ContentHScrollContainer` (the horizontal ScrollViewer). In journal mode, when the user pans horizontally to view the journal column, both the scrollbar and chapter markers scroll off-screen with the content, leaving no scroll position indicator visible.

## Solution

Relocate `ReaderProgressTrack` and `ChapterMarkersCanvas` from inside `InkAreaGrid` to the outer `Grid RowDefinitions="Auto,Auto,*"` at `Grid.Row="2"`. As siblings of `ContentHScrollContainer` at the same grid row, they overlay the content but remain fixed at the viewport's right edge regardless of horizontal scroll position.

## Architecture

### AXAML change only

No code-behind changes required. All pointer handlers (`OnProgressTrackPointerPressed`, `OnProgressTrackPointerMoved`, `OnProgressTrackPointerReleased`, `OnProgressTrackPointerCaptureLost`) use `e.GetPosition(_readerProgressTrack)` — purely track-relative. `UpdateReaderProgress` and `BuildChapterMarkers` both use `_readerProgressTrack.Bounds.Height` — also track-relative. Neither depends on `InkAreaGrid`.

### Element relocation

**Remove from** `InkAreaGrid` (inside `ContentHScrollContainer`):
- `ReaderProgressTrack` (Border, ZIndex=20, HorizontalAlignment=Right, VerticalAlignment=Stretch, Width=12)
- `ChapterMarkersCanvas` (Canvas, ZIndex=19, HorizontalAlignment=Right, VerticalAlignment=Stretch, Width=52, Margin="0,0,14,0")

**Add to** outer `Grid RowDefinitions="Auto,Auto,*"` at `Grid.Row="2"`:
- Same elements, same attributes, add `Grid.Row="2"` attribute to each
- Avalonia renders later Grid children on top of earlier ones at the same row — both overlay `ContentHScrollContainer`

### Positioning

- `ReaderProgressTrack`: right edge flush with viewport right (HorizontalAlignment=Right, no margin), fills full content row height (VerticalAlignment=Stretch)
- `ChapterMarkersCanvas`: right edge 14px from viewport right (Margin="0,0,14,0"), sits to the left of the 12px scrollbar track, fills full content row height

## Files Changed

- `MyBibleApp/Views/MainView.axaml` only

## Out of Scope

- No changes to mobile scrollbar visibility behavior (`PlatformHelper.IsDesktop` guard untouched)
- No changes to scrollbar fade/show-briefly behavior
- No changes to thumb positioning logic or chapter marker building
