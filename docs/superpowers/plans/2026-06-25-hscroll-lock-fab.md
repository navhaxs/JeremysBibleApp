# H-Scroll Lock FAB Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a floating lock button that, when active in journal mode, forces all touch panning to vertical so accidental horizontal scroll can't interrupt reading.

**Architecture:** One bool field `_hScrollLocked` in `MainView.axaml.cs`. The FAB is a `Button` added at the `Panel` root level in `MainView.axaml` (same pattern as `FloatingToolbar`). The axis-resolution line in `OnMarginTouchMoved` checks the flag; journal-mode activation/deactivation shows/hides and resets the button.

**Tech Stack:** Avalonia UI (C# / AXAML), `Material.Icons.Avalonia` (already imported as `xmlns:material` in `MainView.axaml`)

## Global Constraints

- No new NuGet packages — `Material.Icons.Avalonia` already present at v3.0.2
- No new source files — all changes in `MainView.axaml` and `MainView.axaml.cs`
- No changes to trackpad/wheel scroll behavior (`OnHorizontalWheelChanged` untouched)
- No persistence — lock state is session-only, resets when journal mode exits

---

### Task 1: Add FAB button AXAML and wire up code-behind fields

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml` — add Button element inside root Panel
- Modify: `MyBibleApp/Views/MainView.axaml.cs` — add field declarations and FindControl calls

**Interfaces:**
- Produces: `_hScrollLockButton` (Button?), `_hScrollLockIconUnlocked` (Control?), `_hScrollLockIconLocked` (Control?), `_hScrollLocked` (bool) — used in Task 2

---

- [ ] **Step 1: Add FAB button to MainView.axaml**

In `MainView.axaml`, locate the closing `</Panel>` tag (line 963). Insert this block immediately before it (after the DBG `</Button>` at line 961):

```xml
        <!--  ── H-scroll lock FAB (journal mode only) ─────────────────────────  -->
        <Button
            Click="OnHScrollLockButtonClick"
            CornerRadius="22"
            Height="44"
            HorizontalAlignment="Right"
            IsVisible="False"
            Margin="0,0,12,12"
            Opacity="0.85"
            Padding="0"
            ToolTip.Tip="Lock horizontal scroll"
            VerticalAlignment="Bottom"
            Width="44"
            x:Name="HScrollLockButton">
            <Panel>
                <material:MaterialIcon
                    Height="20"
                    Kind="LockOpenOutline"
                    Width="20"
                    x:Name="HScrollLockIconUnlocked" />
                <material:MaterialIcon
                    Height="20"
                    IsVisible="False"
                    Kind="LockOutline"
                    Width="20"
                    x:Name="HScrollLockIconLocked" />
            </Panel>
        </Button>
```

- [ ] **Step 2: Add field declarations to MainView.axaml.cs**

In `MainView.axaml.cs`, locate the debug overlay field block (around line 166–172):

```csharp
    // ── Scroll/chapter debug overlay ──────────────────────────────────────
    private Border? _scrollDebugOverlay;
```

Add these four lines immediately before that comment:

```csharp
    // ── H-scroll lock FAB ─────────────────────────────────────────────────
    private Button? _hScrollLockButton;
    private Control? _hScrollLockIconUnlocked;
    private Control? _hScrollLockIconLocked;
    private bool _hScrollLocked;
```

- [ ] **Step 3: Wire up FindControl calls in OnLoaded**

In `MainView.axaml.cs`, locate the FindControl block for the debug overlay (around line 298–303):

```csharp
        _scrollDebugOverlay = this.FindControl<Border>("ScrollDebugOverlay");
        _dbgStats           = this.FindControl<TextBlock>("DbgStats");
        _dbgEventList       = this.FindControl<ItemsControl>("DbgEventList");
        _debugToggleButton    = this.FindControl<Button>("DebugToggleButton");
        _scrollDebugMenuToggle = this.FindControl<ToggleSwitch>("ScrollDebugToggle");
```

Add three lines immediately before this block:

```csharp
        // ── H-scroll lock FAB ─────────────────────────────────────────────
        _hScrollLockButton       = this.FindControl<Button>("HScrollLockButton");
        _hScrollLockIconUnlocked = this.FindControl<Control>("HScrollLockIconUnlocked");
        _hScrollLockIconLocked   = this.FindControl<Control>("HScrollLockIconLocked");
```

- [ ] **Step 4: Build and verify button exists (no behavior yet)**

Run:
```
dotnet build MyBibleApp/MyBibleApp.csproj
```
Expected: build succeeds, zero errors. The button is hidden at this point (`IsVisible="False"`) — no visual change yet.

- [ ] **Step 5: Commit**

```
git add MyBibleApp/Views/MainView.axaml MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: add hscroll lock FAB button and field wiring"
```

---

### Task 2: Implement H-scroll lock behavior

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs` — click handler, UpdateHScrollLockButton, axis logic change, show/hide on journal mode, reset on journal exit

**Interfaces:**
- Consumes: `_hScrollLockButton` (Button?), `_hScrollLockIconUnlocked` (Control?), `_hScrollLockIconLocked` (Control?), `_hScrollLocked` (bool) from Task 1

---

- [ ] **Step 1: Add click handler and UpdateHScrollLockButton**

In `MainView.axaml.cs`, find the journal button click handler (around line 684):

```csharp
    private void OnJournalsButtonClick(object? sender, RoutedEventArgs e) =>
        JournalsRequested?.Invoke(this, EventArgs.Empty);
```

Add these two methods immediately after it:

```csharp
    private void OnHScrollLockButtonClick(object? sender, RoutedEventArgs e)
    {
        _hScrollLocked = !_hScrollLocked;
        UpdateHScrollLockButton();
    }

    private void UpdateHScrollLockButton()
    {
        if (_hScrollLockIconUnlocked != null) _hScrollLockIconUnlocked.IsVisible = !_hScrollLocked;
        if (_hScrollLockIconLocked   != null) _hScrollLockIconLocked.IsVisible   = _hScrollLocked;
    }
```

- [ ] **Step 2: Modify axis resolution in OnMarginTouchMoved**

In `MainView.axaml.cs`, locate lines 2343–2344 (inside `OnMarginTouchMoved`):

```csharp
        if (_touchPanAxis == PanAxis.Undecided && (Math.Abs(deltaX) > 8 || Math.Abs(deltaY) > 8))
            _touchPanAxis = Math.Abs(deltaX) > Math.Abs(deltaY) ? PanAxis.Horizontal : PanAxis.Vertical;
```

Replace with:

```csharp
        if (_touchPanAxis == PanAxis.Undecided && (Math.Abs(deltaX) > 8 || Math.Abs(deltaY) > 8))
            _touchPanAxis = _hScrollLocked
                ? PanAxis.Vertical
                : (Math.Abs(deltaX) > Math.Abs(deltaY) ? PanAxis.Horizontal : PanAxis.Vertical);
```

- [ ] **Step 3: Show FAB when journal mode activates H-scroll**

In `MainView.axaml.cs`, locate the journal-mode-on branch (around line 2560–2562):

```csharp
                if (_contentHScrollContainer != null)
                    _contentHScrollContainer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                _journalHomePanX = Math.Max(0, LeftBufferDip - layout.LeftMarginDip);
```

Add one line immediately after the `ScrollBarVisibility.Auto` line:

```csharp
                if (_contentHScrollContainer != null)
                    _contentHScrollContainer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                if (_hScrollLockButton != null) _hScrollLockButton.IsVisible = true;
                _journalHomePanX = Math.Max(0, LeftBufferDip - layout.LeftMarginDip);
```

- [ ] **Step 4: Reset and hide FAB when journal mode deactivates**

In `MainView.axaml.cs`, locate the journal-mode-off branch (around line 2547–2550):

```csharp
            if (_contentHScrollContainer != null)
                _contentHScrollContainer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _journalHomePanX = 0;
            _journalHScrollNeedsReset = false;
```

Add three lines immediately after the `ScrollBarVisibility.Disabled` line:

```csharp
            if (_contentHScrollContainer != null)
                _contentHScrollContainer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _hScrollLocked = false;
            UpdateHScrollLockButton();
            if (_hScrollLockButton != null) _hScrollLockButton.IsVisible = false;
            _journalHomePanX = 0;
            _journalHScrollNeedsReset = false;
```

- [ ] **Step 5: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```
Expected: zero errors.

- [ ] **Step 6: Manual verification**

1. Run the app on a touch device or tablet.
2. Open a journal — the lock FAB appears bottom-right with an open-lock icon.
3. Tap the FAB — icon switches to closed lock.
4. Swipe vertically on the scripture text — scrolls vertically even with a slightly diagonal start.
5. Swipe horizontally — does NOT pan to journal column while locked.
6. Tap the FAB again — icon switches back to open-lock, horizontal swipe works again.
7. Close the journal — FAB hides, lock resets to unlocked state.

- [ ] **Step 7: Commit**

```
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: implement hscroll lock toggle behavior for journal mode"
```
