# H-Scroll Lock FAB — Design Spec

**Date:** 2026-06-25  
**Status:** Approved

## Problem

In journal mode, `ContentHScrollContainer` enables horizontal scrolling so the user can pan between the Bible text column and the journal column. Touch axis detection in `OnMarginTouchMoved` (MainView.axaml.cs:2343) resolves direction from the first 8px of movement — diagonal starts accidentally lock into horizontal scroll, making reliable vertical scrolling difficult on mobile.

## Solution

A floating action button (FAB) overlaid on the content area that toggles a horizontal scroll lock. When locked, all touch panning is forced vertical regardless of movement direction.

## Architecture

### State

Add one field to `MainView.axaml.cs`:

```csharp
private bool _hScrollLocked;
```

### Touch handler change

In `OnMarginTouchMoved`, modify the axis-resolution block:

```csharp
if (_touchPanAxis == PanAxis.Undecided && (Math.Abs(deltaX) > 8 || Math.Abs(deltaY) > 8))
    _touchPanAxis = _hScrollLocked
        ? PanAxis.Vertical
        : (Math.Abs(deltaX) > Math.Abs(deltaY) ? PanAxis.Horizontal : PanAxis.Vertical);
```

When `_hScrollLocked` is true, axis resolves immediately to `Vertical` regardless of delta direction.

### Reset on journal exit

In the journal-mode-off branch (where `HorizontalScrollBarVisibility = Disabled` is set, ~line 2547):

```csharp
_hScrollLocked = false;
UpdateHScrollLockButton();
```

### No change to trackpad/wheel scroll

`OnHorizontalWheelChanged` is unaffected — deliberate wheel-based horizontal scroll continues to work.

## UI

### FAB button (MainView.axaml)

Placed inside the outer `Grid RowDefinitions="Auto,Auto,*"` at `Grid.Row="2"`, same row as `ContentHScrollContainer`. Avalonia renders later children on top.

- Size: 44×44, `CornerRadius="22"` (circular)
- Position: `HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,12,12"`
- ZIndex: high enough to sit above content (default stacking order of later sibling suffices)
- `IsVisible`: controlled from code-behind, set to `true` when journal mode activates H-scroll, `false` otherwise
- Icon: `MaterialIcon Kind="LockOpenOutline"` (unlocked) / `Kind="LockOutline"` (locked)
- `x:Name="HScrollLockButton"`
- Opacity: 0.85 to avoid feeling intrusive

### Code-behind toggle handler

```csharp
private void OnHScrollLockButtonClick(object? sender, RoutedEventArgs e)
{
    _hScrollLocked = !_hScrollLocked;
    UpdateHScrollLockButton();
}

private void UpdateHScrollLockButton()
{
    if (_hScrollLockButton == null) return;
    var icon = _hScrollLockButton.Content as MaterialIcon;
    if (icon != null)
        icon.Kind = _hScrollLocked ? MaterialIconKind.Lock : MaterialIconKind.LockOpenOutline;
}
```

## Behavior Summary

| State | Touch pan result |
|-------|-----------------|
| Journal off | Button hidden, H-scroll always disabled |
| Journal on, unlocked | Existing 8px axis detection (current behavior) |
| Journal on, locked | All touch panning forced vertical |
| Journal exits | Lock resets to unlocked, button hides |

## Files Changed

- `MyBibleApp/Views/MainView.axaml` — add FAB button element at Grid.Row="2"
- `MyBibleApp/Views/MainView.axaml.cs` — `_hScrollLocked` field, `OnHScrollLockButtonClick`, `UpdateHScrollLockButton`, modify `OnMarginTouchMoved` axis logic, reset on journal exit

## Out of Scope

- No effect on trackpad/mouse wheel horizontal scroll
- No snap-to-column behavior when locking
- No persistence of lock state across sessions
