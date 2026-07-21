# Fix Scroll Jump from Window Trim/Extend Compensation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate visible scroll position jumps when `TrimWindowTop` and `ExtendWindowUp` apply their offset compensation while the custom inertia engine is running.

**Architecture:**  
Both methods currently set `_paragraphScrollViewer.Offset` immediately. A concurrent inertia tick (`DispatcherTimer`) fires at higher dispatcher priority and overwrites that value before the layout commit, silently discarding the compensation. Fix: move ALL top-compensation into `OnParagraphListLayoutUpdated`, applying the delta against the offset that actually landed (inertia + trim combined), never racing. Extent-based measurement in `LayoutUpdated` also eliminates the `60px × paragraphCount` estimation error (22 500px for Psalms 119).

**Tech Stack:** C# / Avalonia UI — `MainView.axaml.cs` only; no new files.

## Global Constraints

- All changes in `MyBibleApp/Views/MainView.axaml.cs`.
- Do not touch `TrimWindowBottom` or `ExtendWindowDown` — they add/remove at the bottom and need no scroll compensation.
- Do not add any `await` or `Task.Delay` inside the compensation path — must stay synchronous on the UI thread.
- `_chapterStartY` / `_chapterLocalTops` cache stays; `RebuildParagraphTopCache()` already rebuilds from scratch on every `LayoutUpdated` — cache-shift lines in trim/extend are removed (no longer needed when compensation lands atomically in the same `LayoutUpdated` call).
- Build must stay green (`dotnet build MyBibleApp/MyBibleApp.csproj --no-restore -v q`) with 0 errors after each task.

---

### Task 1: Add deferred-compensation fields and apply them in `OnParagraphListLayoutUpdated`

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

**What this does:** Wires the apply-site plumbing without touching the produce-site yet. After this task, `_pendingTopTrimCompensation` and `_pendingTopExtentBeforeAdd` exist and are applied in `LayoutUpdated`; they are always zero/unset so behaviour is unchanged.

- [ ] **Step 1: Add two new fields beside the other `_isAdjustingWindow` flags (~line 157)**

Find this block:
```csharp
    private bool _isAdjustingWindow;
    private int _windowCheckVersion;
    private bool _immediateExtendPending;
```
Replace with:
```csharp
    private bool _isAdjustingWindow;
    private int _windowCheckVersion;
    private bool _immediateExtendPending;
    // Deferred top-compensation: applied in OnParagraphListLayoutUpdated to avoid
    // racing the inertia timer (which fires at higher dispatch priority than Loaded).
    private double _pendingTopTrimCompensation;   // > 0 → subtract from Offset after trim
    private double _pendingTopExtentBeforeAdd;    // ≥ 0 → extent before up-extend; -1 = none
```

- [ ] **Step 2: Apply pending compensation at the TOP of `OnParagraphListLayoutUpdated`, before `RebuildParagraphTopCache`**

Find (around line 434):
```csharp
    private void OnParagraphListLayoutUpdated(object? sender, EventArgs e)
    {
        EnsureScrollTrackingAttached();
        RebuildParagraphTopCache();
    }
```
Replace with:
```csharp
    private void OnParagraphListLayoutUpdated(object? sender, EventArgs e)
    {
        EnsureScrollTrackingAttached();
        ApplyPendingTopCompensation();
        RebuildParagraphTopCache();
    }

    private void ApplyPendingTopCompensation()
    {
        var sv = _paragraphScrollViewer;
        if (sv == null) return;

        // TrimWindowTop deferred: subtract measured removed height from wherever
        // inertia + natural scroll landed while the layout was pending.
        if (_pendingTopTrimCompensation > 0)
        {
            var delta = _pendingTopTrimCompensation;
            _pendingTopTrimCompensation = 0;
            var newOff = Math.Max(0, sv.Offset.Y - delta);
            DbgLog($"  ↳ trim-compensate  Δ=-{delta:F0}px  off:{sv.Offset.Y:F0}→{newOff:F0}");
            sv.Offset = new Vector(sv.Offset.X, newOff);
        }

        // ExtendWindowUp deferred: add the ACTUAL extent increase (beats estimate).
        if (_pendingTopExtentBeforeAdd >= 0)
        {
            var extBefore = _pendingTopExtentBeforeAdd;
            _pendingTopExtentBeforeAdd = -1;
            var actualAdded = sv.Extent.Height - extBefore;
            if (actualAdded > 0)
            {
                var newOff = sv.Offset.Y + actualAdded;
                DbgLog($"  ↳ up-compensate    Δ=+{actualAdded:F0}px  off:{sv.Offset.Y:F0}→{newOff:F0}  (extent Δ)");
                sv.Offset = new Vector(sv.Offset.X, newOff);
            }
        }
    }
```

- [ ] **Step 3: Initialize `_pendingTopExtentBeforeAdd = -1` in the constructor (it must be −1, not 0, to mean "not set")**

Find the constructor:
```csharp
    public MainView()
    {
        InitializeComponent();
```
Add the initialization immediately after `InitializeComponent();`:
```csharp
        _pendingTopExtentBeforeAdd = -1;
```

- [ ] **Step 4: Build and verify no errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj --no-restore -v q
```
Expected: `0 Error(s)`. The app behaviour is identical to before (both fields start at their "idle" values).

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "refactor: add deferred top-compensation plumbing in LayoutUpdated"
```

---

### Task 2: Switch `TrimWindowTop` to deferred compensation

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs:1519-1555` (TrimWindowTop)

**What this does:** Instead of setting `_paragraphScrollViewer.Offset` directly (races inertia), records the measured height as `_pendingTopTrimCompensation` and removes the now-redundant `_chapterStartY` shift. `ApplyPendingTopCompensation` (Task 1) applies it in the same `LayoutUpdated` that sees the new layout, regardless of where inertia moved the view in the meantime.

- [ ] **Step 1: Read current `TrimWindowTop` to identify exact old strings**

Read lines 1519–1560 of `MainView.axaml.cs` and confirm the code matches what is shown below before editing.

- [ ] **Step 2: Remove the immediate Offset set and `_chapterStartY` shift; accumulate pending delta**

Find this block (the bottom half of TrimWindowTop, starting after `_windowStart++`):
```csharp
        // Clear stale position cache so orphaned strokes fall back to delta=0.
        _chapterStartY.Remove(chapter);
        _chapterLocalTops.Remove(chapter);

        // Compensate scroll offset downward.
        var oldScrollOffset = _paragraphScrollViewer.Offset.Y;
        var newOffset = Math.Max(0, oldScrollOffset - removedHeight);
        DbgLog($"-ch{chapter} ↑trim  ht={removedHeight:F0}px [{(measured.HasValue ? "meas" : "est ")}]  off:{oldScrollOffset:F0}→{newOffset:F0}  Δ={newOffset - oldScrollOffset:+F0;-F0}  win={_windowStart}..{_windowEnd}");
        _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, newOffset);

        // Shift cached content-Y values to match the new scroll coordinate system.
        // Same race as ExtendWindowUp: offset change fires OnParagraphScrollChanged
        // synchronously, but LayoutUpdated → RebuildParagraphTopCache fires later.
        var scrollDelta = newOffset - oldScrollOffset;
        foreach (var key in _chapterStartY.Keys.ToList())
            _chapterStartY[key] += scrollDelta;

        ChapterExitedWindow?.Invoke(this, chapter);
```
Replace with:
```csharp
        // Clear stale position cache so orphaned strokes fall back to delta=0.
        _chapterStartY.Remove(chapter);
        _chapterLocalTops.Remove(chapter);

        // Defer offset compensation: applying it now races the inertia DispatcherTimer
        // (higher dispatch priority). ApplyPendingTopCompensation() in LayoutUpdated
        // applies it against the actual offset after inertia and layout have settled.
        _pendingTopTrimCompensation += removedHeight;
        DbgLog($"-ch{chapter} ↑trim  ht={removedHeight:F0}px [{(measured.HasValue ? "meas" : "est ")}]  pending={_pendingTopTrimCompensation:F0}px  win={_windowStart}..{_windowEnd}");

        ChapterExitedWindow?.Invoke(this, chapter);
```

- [ ] **Step 3: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj --no-restore -v q
```
Expected: `0 Error(s)`.

- [ ] **Step 4: Run the app, open the debug overlay (app menu → "Scroll debug overlay"), scroll fast through several chapters**

Confirm in the event log:
- Every `-ch↑trim` line is now immediately followed by a `↳ trim-compensate` line from LayoutUpdated.
- `⚡JUMP` events that exactly matched trim heights (e.g., Δ=−181) should disappear — the compensation now happens outside the scroll path so the jump detector doesn't see it as a jump.
- Scroll still feels smooth.

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: defer TrimWindowTop offset compensation to LayoutUpdated to avoid inertia race"
```

---

### Task 3: Switch `ExtendWindowUp` to extent-based deferred compensation

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs:1485-1512` (ExtendWindowUp)

**What this does:** Eliminates the `paragraphCount × 60px` estimation error (e.g., 22 500px for Psalms 119). Before prepending items, snapshot `Extent.Height`. In `LayoutUpdated`, the actual extent increase is the compensation — exact, no estimation needed. Also removes the `_chapterStartY` interim shift (no longer needed because compensation and cache rebuild happen in the same `LayoutUpdated`).

- [ ] **Step 1: Read current `ExtendWindowUp` (lines ~1485–1512) and confirm code matches before editing**

- [ ] **Step 2: Replace the Offset-set and `_chapterStartY`-shift block with extent snapshot**

Find the current body of `ExtendWindowUp` after `_windowStart--`:
```csharp
        _windowStart--;
        var chapter = _windowStart + 1;     // 1-based
        var newParagraphs = _chapterGroups[_windowStart];
        var estimatedHeight = _measuredChapterHeights.TryGetValue(chapter, out var cachedHeight)
            ? cachedHeight
            : EstimateChapterHeight(chapter);

        // Prepend paragraphs (ObservableCollection has no AddRange; insert individually).
        for (var i = newParagraphs.Count - 1; i >= 0; i--)
            _windowedItems.Insert(0, PrepareForDisplay(newParagraphs[i]));

        // Compensate scroll offset so visible content doesn't jump.
        var oldOffset = _paragraphScrollViewer.Offset.Y;
        var newOffset = oldOffset + estimatedHeight;
        DbgLog($"+ch{chapter} ↑up  ht={estimatedHeight:F0}px [{(_measuredChapterHeights.ContainsKey(chapter) ? "cached" : "est   ")}]  off:{oldOffset:F0}→{newOffset:F0}  win={_windowStart + 1}..{_windowEnd}");
        _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, newOffset);

        // Shift cached content-Y values to match the new scroll coordinate system.
        // Without this, the ink drift callback returns stale values between the offset
        // change and the next LayoutUpdated → RebuildParagraphTopCache cycle, causing
        // strokes to render shifted by ~estimatedHeight during fast scrolling.
        foreach (var key in _chapterStartY.Keys.ToList())
            _chapterStartY[key] += estimatedHeight;

        ChapterEnteredWindow?.Invoke(this, chapter);
```
Replace with:
```csharp
        _windowStart--;
        var chapter = _windowStart + 1;     // 1-based
        var newParagraphs = _chapterGroups[_windowStart];

        // Snapshot extent BEFORE insert so LayoutUpdated can compute actual added height.
        // Using actual extent delta eliminates the paragraphCount×60px estimation error
        // (which is 10–100× wrong for short Psalms chapters or Psalms 119 at 22 500px).
        _pendingTopExtentBeforeAdd = _paragraphScrollViewer.Extent.Height;

        // Prepend paragraphs (ObservableCollection has no AddRange; insert individually).
        for (var i = newParagraphs.Count - 1; i >= 0; i--)
            _windowedItems.Insert(0, PrepareForDisplay(newParagraphs[i]));

        DbgLog($"+ch{chapter} ↑up  [deferred extent-compensation]  win={_windowStart + 1}..{_windowEnd}");

        ChapterEnteredWindow?.Invoke(this, chapter);
```

- [ ] **Step 3: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj --no-restore -v q
```
Expected: `0 Error(s)`.

- [ ] **Step 4: Run the app, scroll DOWN through several chapters, then scroll BACK UP past Psalms 119 (or any multi-verse chapter)**

Confirm in the debug overlay:
- Every `+ch↑up` is followed by a `↳ up-compensate Δ=+NNNpx (extent Δ)` line.
- The Δ value is the actual rendered height, not the old 22 500px estimate.
- Scrolling back up through Psalms 119 does **not** teleport the view.
- `off:` in the stats panel stays stable during up-scrolling (no multi-thousand-pixel jumps).

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: use actual extent delta for ExtendWindowUp compensation, eliminating estimation error"
```

---

### Task 4: Suppress false-positive ⚡JUMP events from compensation scrolls

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

**What this does:** `ApplyPendingTopCompensation` sets `_paragraphScrollViewer.Offset` which fires `OnParagraphScrollChanged` synchronously. That will trip the `⚡JUMP` detector (delta > 150px) even though it's an expected, controlled move. Suppress it with a guard flag so the JUMP log only shows genuinely unexpected jumps.

- [ ] **Step 1: Add a guard field next to the other pending-compensation fields**

Find:
```csharp
    private double _pendingTopTrimCompensation;   // > 0 → subtract from Offset after trim
    private double _pendingTopExtentBeforeAdd;    // ≥ 0 → extent before up-extend; -1 = none
```
Replace with:
```csharp
    private double _pendingTopTrimCompensation;   // > 0 → subtract from Offset after trim
    private double _pendingTopExtentBeforeAdd;    // ≥ 0 → extent before up-extend; -1 = none
    private bool _isApplyingWindowCompensation;   // suppresses ⚡JUMP detector during controlled compensation
```

- [ ] **Step 2: Set the guard flag around the Offset sets in `ApplyPendingTopCompensation`**

Find in `ApplyPendingTopCompensation`:
```csharp
            DbgLog($"  ↳ trim-compensate  Δ=-{delta:F0}px  off:{sv.Offset.Y:F0}→{newOff:F0}");
            sv.Offset = new Vector(sv.Offset.X, newOff);
```
Replace with:
```csharp
            DbgLog($"  ↳ trim-compensate  Δ=-{delta:F0}px  off:{sv.Offset.Y:F0}→{newOff:F0}");
            _isApplyingWindowCompensation = true;
            sv.Offset = new Vector(sv.Offset.X, newOff);
            _isApplyingWindowCompensation = false;
```

Find:
```csharp
                DbgLog($"  ↳ up-compensate    Δ=+{actualAdded:F0}px  off:{sv.Offset.Y:F0}→{newOff:F0}  (extent Δ)");
                sv.Offset = new Vector(sv.Offset.X, newOff);
```
Replace with:
```csharp
                DbgLog($"  ↳ up-compensate    Δ=+{actualAdded:F0}px  off:{sv.Offset.Y:F0}→{newOff:F0}  (extent Δ)");
                _isApplyingWindowCompensation = true;
                sv.Offset = new Vector(sv.Offset.X, newOff);
                _isApplyingWindowCompensation = false;
```

- [ ] **Step 3: Gate the ⚡JUMP detector on the flag**

Find in `OnParagraphScrollChanged`:
```csharp
        // Debug: flag large unexpected jumps (distinguish from normal momentum).
        if (_scrollDebugOverlay?.IsVisible == true)
        {
            var delta = currentOffset - _lastScrollOffset;
            if (Math.Abs(delta) > 150 && elapsed is > 0 and < 0.5)
                DbgLog($"⚡JUMP  {_lastScrollOffset:F0}→{currentOffset:F0}  Δ={delta:+F0;-F0}");
            DbgUpdateStats();
        }
```
Replace with:
```csharp
        // Debug: flag large unexpected jumps (distinguish from normal momentum).
        if (_scrollDebugOverlay?.IsVisible == true)
        {
            var delta = currentOffset - _lastScrollOffset;
            if (Math.Abs(delta) > 150 && elapsed is > 0 and < 0.5 && !_isApplyingWindowCompensation)
                DbgLog($"⚡JUMP  {_lastScrollOffset:F0}→{currentOffset:F0}  Δ={delta:+F0;-F0}");
            DbgUpdateStats();
        }
```

- [ ] **Step 4: Also fix the format string so "Δ=-F181" renders as "Δ=-181"**

The current format `{delta:+F0;-F0}` outputs "F" as a literal character in the negative section (C# custom format sections treat `F` as literal, not fixed-point). Fix by using a standard format instead:

Find (same block, just edited above):
```csharp
                DbgLog($"⚡JUMP  {_lastScrollOffset:F0}→{currentOffset:F0}  Δ={delta:+F0;-F0}");
```
Replace with:
```csharp
                DbgLog($"⚡JUMP  {_lastScrollOffset:F0}→{currentOffset:F0}  Δ={delta:+0;-0}px");
```

- [ ] **Step 5: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj --no-restore -v q
```
Expected: `0 Error(s)`.

- [ ] **Step 6: Run the app with the debug overlay open and scroll fast**

Confirm:
- `⚡JUMP` entries are now rare or absent during normal fast-scroll (compensation scrolls no longer appear as jumps).
- Any remaining `⚡JUMP` entries represent genuinely unexpected position changes worth investigating.
- Δ values in JUMP entries now read e.g. `Δ=-181px` (not `Δ=-F181`).

- [ ] **Step 7: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: suppress false-positive JUMP events from controlled window compensation scrolls"
```

---

## Self-Review

**Spec coverage:**
| Root cause | Task |
|---|---|
| Inertia overwrites TrimWindowTop compensation | Task 2 |
| EstimateChapterHeight 10–100× wrong for ExtendWindowUp | Task 3 |
| ⚡JUMP fires on controlled compensation scrolls | Task 4 |
| Plumbing (fields + apply site) | Task 1 |

**Placeholder scan:** No TBD/TODO/placeholder text. All code blocks are complete.

**Type consistency:** `_pendingTopTrimCompensation` (double) and `_pendingTopExtentBeforeAdd` (double) used consistently. `_isApplyingWindowCompensation` (bool) introduced in Task 4, gate added in same task.

**Edge case — `_pendingTopExtentBeforeAdd` unset path:** Initialised to `-1` in constructor (Task 1 Step 3). Guard in `ApplyPendingTopCompensation` checks `>= 0` before using. If `ExtendWindowUp` is never called, field stays `−1` and the branch is skipped. ✓

**Edge case — multiple TrimWindowTop calls before LayoutUpdated:** Field is `+=`, so multiple trims accumulate correctly. ✓

**Edge case — `ExtendWindowUp` called from `CheckWindowExtend` vs `CheckWindowBounds`:** Both paths now set `_pendingTopExtentBeforeAdd`; `ApplyPendingTopCompensation` is called in every `LayoutUpdated` regardless of which code path triggered the add. ✓
