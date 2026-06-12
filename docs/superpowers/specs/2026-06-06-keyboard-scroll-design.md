# Keyboard Scroll Design

**Date:** 2026-06-06  
**Status:** Approved  
**Scope:** `MyBibleApp/Views/MainView.axaml.cs` only (~20 lines added)

## Feature

Arrow Up/Down and Page Up/Down keys scroll the Bible text reader when the text area has focus.

## Architecture

No new files, no new abstractions. One tunnel `KeyDown` handler registered on `MainView` in code-behind.

## Components

### Registration

In `MainView.axaml.cs`, after the view is loaded, register a tunnel handler on the view itself:

```csharp
this.AddHandler(KeyDownEvent, OnReaderKeyDown, RoutingStrategies.Tunnel);
```

Tunnel routing fires before `ParagraphList` (ListBox) consumes the arrow keys for item selection, giving us first-crack at the event.

### Handler: `OnReaderKeyDown`

```
OnReaderKeyDown(sender, e):
  1. Get focused control from FocusManager
  2. If focused control is not ParagraphList and not a visual descendant of ParagraphList → return
  3. If _paragraphScrollViewer is null → return
  4. Compute delta:
       Key.Up       → -50px
       Key.Down     → +50px
       Key.PageUp   → -Viewport.Height
       Key.PageDown → +Viewport.Height
       other keys   → return (do not steal)
  5. maxY = Extent.Height - Viewport.Height
     newY = Clamp(Offset.Y + delta, 0, maxY)
     _paragraphScrollViewer.Offset = new Vector(0, newY)
     e.Handled = true
```

### Focus Check

```csharp
var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
bool inReader = focused == _paragraphList || (_paragraphList?.IsAncestorOf(focused) ?? false);
```

User must click the text area once to give it focus. After that, keys scroll until focus moves elsewhere (e.g., clicking the lookup box).

## Scroll Increments

| Key | Delta |
|-----|-------|
| Arrow Up / Down | 50px (matches existing mouse wheel step) |
| Page Up / Down | `_paragraphScrollViewer.Viewport.Height` |

## Data Flow

```
KeyDown (tunnel) → OnReaderKeyDown
  → focus check passes
  → compute delta
  → set _paragraphScrollViewer.Offset
  → triggers OnParagraphScrollChanged (existing handler)
  → updates ink overlay, reader progress, window bounds (all existing behaviour)
```

The scroll fires the existing `ScrollChanged` event chain automatically — no special handling needed.

## Error Handling

- `_paragraphScrollViewer` null check guards against pre-layout access
- `Math.Clamp` prevents scrolling past content bounds
- Non-matching keys fall through untouched

## Testing

1. Click Bible text → press ↓ repeatedly → text scrolls down 50px/press
2. Press ↑ → text scrolls up 50px/press, stops at top
3. Press Page Down → jumps one full viewport
4. Press Page Up → jumps one full viewport up, stops at top
5. Click search/lookup box → arrow keys do not scroll text
6. Scroll to near-bottom → Page Down clamps at max, no overscroll
