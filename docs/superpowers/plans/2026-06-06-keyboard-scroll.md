# Keyboard Scroll Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Arrow Up/Down and Page Up/Down keys scroll the Bible text when the reader has focus.

**Architecture:** One tunnel `KeyDown` handler registered on `MainView` in `OnLoaded`. Focus check gates on `_paragraphList` subtree. Scroll logic reuses the same `Offset` pattern as the existing mouse wheel handler.

**Tech Stack:** Avalonia, C# — `RoutingStrategies.Tunnel`, `FocusManager`, `ScrollViewer.Offset`

---

### Task 1: Add keyboard scroll handler to MainView

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

- [ ] **Step 1: Register tunnel KeyDown handler in `OnLoaded`**

In `OnLoaded`, after the line `_paragraphList = this.FindControl<ListBox>("ParagraphList");`, add:

```csharp
this.AddHandler(KeyDownEvent, OnReaderKeyDown, RoutingStrategies.Tunnel);
```

Full context for where to insert (around line 201):

```csharp
_paragraphList  = this.FindControl<ListBox>("ParagraphList");
if (_paragraphList != null)
{
    _paragraphList.ItemsSource = _windowedItems;
    _paragraphList.AddHandler(
        Controls.CrossReferenceBlock.ReferenceClickedEvent,
        OnCrossReferenceClicked);
}
// ADD THIS LINE:
this.AddHandler(KeyDownEvent, OnReaderKeyDown, RoutingStrategies.Tunnel);
```

- [ ] **Step 2: Add `OnReaderKeyDown` handler method**

Add the following method in the "Mouse/touch input" region near `OnMarginMouseWheelChanged` (around line 2145). Place it immediately before or after that method:

```csharp
private void OnReaderKeyDown(object? sender, KeyEventArgs e)
{
    if (_paragraphScrollViewer == null || _paragraphList == null) return;

    var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
    bool inReader = focused != null && (focused == _paragraphList || _paragraphList.IsAncestorOf(focused));
    if (!inReader) return;

    double delta = e.Key switch
    {
        Key.Up       => -50.0,
        Key.Down     => +50.0,
        Key.PageUp   => -_paragraphScrollViewer.Viewport.Height,
        Key.PageDown => +_paragraphScrollViewer.Viewport.Height,
        _            => 0.0,
    };

    if (delta == 0.0) return;

    var maxY = Math.Max(0, _paragraphScrollViewer.Extent.Height - _paragraphScrollViewer.Viewport.Height);
    _paragraphScrollViewer.Offset = new Vector(
        _paragraphScrollViewer.Offset.X,
        Math.Clamp(_paragraphScrollViewer.Offset.Y + delta, 0, maxY));
    e.Handled = true;
}
```

- [ ] **Step 3: Build and verify it compiles**

```powershell
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: build succeeds with no errors.

- [ ] **Step 4: Manual test**

Run the app (Desktop target). Perform each check:

| # | Action | Expected |
|---|--------|----------|
| 1 | Click Bible text area | Focus lands on reader |
| 2 | Press ↓ repeatedly | Text scrolls down ~50px per press |
| 3 | Press ↑ repeatedly | Text scrolls up ~50px per press, stops at top |
| 4 | Press Page Down | Jumps one full viewport down |
| 5 | Press Page Up | Jumps one full viewport up, stops at top |
| 6 | Scroll near bottom → press Page Down | Clamps at bottom, no overscroll |
| 7 | Click lookup/search textbox → press ↓ | Text does NOT scroll |

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: add arrow and page key scrolling to Bible reader"
```
