# Pointer Input Mode — Design Spec

## Goal

Add a toggle button to the annotation toolbar that lets mouse (`PointerType.Mouse`) events draw ink strokes. Targets Windows tablets where the stylus is reported as a mouse rather than a pen by the OS.

## Background

The annotation system currently filters all input to `PointerType.Pen` at three sites. On some Windows tablets, stylus input reports as `PointerType.Mouse`. Those devices cannot draw without this mode.

Scrolling is unaffected: `TouchOnlyScrollGestureRecognizer` already ignores mouse events, so mouse falls through to the ink canvas cleanly when the mode is enabled.

`ParagraphInkCanvas.ShouldStartInking` is debug/dev-only (`DebugDrawingView`); not part of the live annotation path. No change needed there.

---

## Architecture

### Flag

`AllowMouseInput` — `bool` property on `InkOverlayCanvas`. Default `false`. Session-only (plain property, no settings persistence). Survives annotation mode off/on cycles within the session.

### Helper method on `InkOverlayCanvas`

```csharp
private bool IsAcceptedPointerType(PointerType type) =>
    type == PointerType.Pen || (AllowMouseInput && type == PointerType.Mouse);
```

Centralises the acceptance logic so both pointer event overrides use a single expression.

### Filter sites

| File | Method | Line | Change |
|------|--------|------|--------|
| `MyBibleApp/Controls/InkOverlayCanvas.cs` | `OnPointerMoved` | ~468 | `Type != Pen → return` → `!IsAcceptedPointerType(type) → return` |
| `MyBibleApp/Controls/InkOverlayCanvas.cs` | `OnPointerReleased` | ~485 | same |
| `MyBibleApp/Views/MainView.axaml.cs` | `OnListBoxPenPressed` | ~484 | inline equivalent: pass if `Type == Pen` OR `(AllowMouseInput && Type == Mouse)` |

`_penUnderlay` — verified during implementation. If it owns pointer event handlers with the same filter, same treatment. If visual-only, no change.

---

## Toolbar Button

**Placement:** End of `AnnotationSection` StackPanel, after `CustomColorButton`.

**AXAML:**
```xml
<!-- Pointer input mode -->
<ToggleButton x:Name="PointerModeButton"
              Classes="tool-btn"
              ToolTip.Tip="Use pointer/mouse to draw"
              IsCheckedChanged="OnPointerModeIsCheckedChanged">
  <material:MaterialIcon Kind="CursorDefault" Width="20" Height="20" />
</ToggleButton>
```

**Code-behind:**
- Field: `private ToggleButton? _pointerModeButton;`
- `FindControl<ToggleButton>("PointerModeButton")` in the control-find block alongside other toolbar controls
- Handler:

```csharp
private void OnPointerModeIsCheckedChanged(object? sender, RoutedEventArgs e)
{
    if (_inkOverlay == null) return;
    _inkOverlay.AllowMouseInput = _pointerModeButton?.IsChecked == true;
}
```

**Behaviour:**
- Independent of Pen / Highlighter / Eraser mode — does not deselect or interfere with them
- Toggling annotation mode off and back on leaves `PointerModeButton` checked state intact
- No persistence across app restarts

---

## What Does Not Change

- Scroll behaviour — `TouchOnlyScrollGestureRecognizer` already ignores mouse; no swap needed
- Touch input — still scrolls only, does not draw
- Pen input — always draws regardless of toggle state
- `ParagraphInkCanvas` — debug view only, untouched

---

## Files Modified

| File | Change |
|------|--------|
| `MyBibleApp/Controls/InkOverlayCanvas.cs` | Add `AllowMouseInput` property, `IsAcceptedPointerType` helper, update two guards |
| `MyBibleApp/Views/MainView.axaml` | Add `PointerModeButton` toggle at end of `AnnotationSection` |
| `MyBibleApp/Views/MainView.axaml.cs` | Add field, FindControl, handler, update `OnListBoxPenPressed` guard |
