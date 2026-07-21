# Fix Chapter Window Oscillation and Format Bugs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate visible scroll jumps and infinite Trim↔Extend oscillation loops when chapters load/unload during slow upward scrolling.

**Architecture:** All windowing logic lives in `MainView.axaml.cs`. Three independent fixes: (1) cache measured chapter heights so `ExtendWindowUp` doesn't undercompensate scroll offset, (2) add visibility-aware guards in `CheckWindowBounds` so it never trims a chapter that `CheckWindowExtend` would immediately re-add, (3) fix an invalid C# numeric format string in debug logging.

**Tech Stack:** C# / Avalonia UI. No new dependencies. All changes in one file.

---

## Root Cause Summary (from debug log)

```
Bug 1 — TrimTop uses measured=2603px, ExtendUp uses estimate=2100px → 503px jump per cycle
Bug 2 — TrimBottom removes ch3, CheckExtend re-adds ch3, repeat ×40+ (infinite spin at same scrollTop)
Bug 3 — {error:+F1;-F1;0} is invalid C# format → log shows literal "+F1" not the value
```

Actual measured heights vs estimates (from log):
- ch1: estimate=960, actual=2589 → 2.7× off
- ch2: estimate=660, actual=2213 → 3.4× off
- ch3: estimate=2100, actual=2603 → 1.24× off

---

## Files

| File | Change |
|------|--------|
| `MyBibleApp/Views/MainView.axaml.cs` | All 4 tasks — no other files touched |

---

## Task 1: Fix format string (Bug 3)

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs` — two `#if DEBUG` `Debug.WriteLine` calls

The format specifier `{error:+F1;-F1;0}` is invalid C# — `F` is not a valid pattern character in three-section numeric format. Both debug log callsites produce literal text `+F1` instead of a number.

Valid replacement: `{error:+0.0;-0.0;0.0}` where `+` and `-` are literal prefix characters, `0.0` is the digit pattern.

- [ ] **Step 1: Fix the TrimTop estimateError format string**

In `TrimWindowTop`, find:
```csharp
Debug.WriteLine(
    $"[WIN] TrimTop ch={chapter} estimateError={error:+F1;-F1;0} px " +
    $"(measured-estimate; positive=was too low, negative=was too high)");
```
Replace with:
```csharp
Debug.WriteLine(
    $"[WIN] TrimTop ch={chapter} estimateError={error:+0.0;-0.0;0.0}px " +
    $"(measured-estimate; positive=was too low, negative=was too high)");
```

- [ ] **Step 2: Fix the PostLayout error format string**

In `OnParagraphListLayoutUpdated`, find:
```csharp
Debug.WriteLine(
    $"[WIN] PostLayout ch={_dbgLastExtendUpChapter} " +
    $"estimate={_dbgLastExtendUpEstimate:F1} actual={actual.Value:F1} " +
    $"error={error:+F1;-F1;0}px " +
    $"(positive=underestimated→jumped down, negative=overestimated→jumped up) " +
    $"currentOffset={currentOffset:F1}");
```
Replace with:
```csharp
Debug.WriteLine(
    $"[WIN] PostLayout ch={_dbgLastExtendUpChapter} " +
    $"estimate={_dbgLastExtendUpEstimate:F1} actual={actual.Value:F1} " +
    $"error={error:+0.0;-0.0;0.0}px " +
    $"(positive=underestimated→jumped down, negative=overestimated→jumped up) " +
    $"currentOffset={currentOffset:F1}");
```

- [ ] **Step 3: Build and verify format fix**

```
dotnet build MyBibleApp/MyBibleApp.csproj --nologo -v quiet
```
Expected: `0 Error(s)`

Run the app in Debug, scroll upward. Log must now show:
```
[WIN] TrimTop ch=3 estimateError=+503.0px (measured-estimate; ...)
[WIN] PostLayout ch=3 ... error=+503.0px ...
```
Not `+F1`.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: correct invalid numeric format string in debug logging

{error:+F1;-F1;0} is not valid C# — F is not a pattern char.
Replaced with {error:+0.0;-0.0;0.0} in TrimTop and PostLayout logs."
```

---

## Task 2: Cache measured heights when trimming top (Bug 1, part A)

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs` — add field + populate in `TrimWindowTop`

When `TrimWindowTop` fires, it already calls `MeasureChapterHeight` and has the accurate rendered height. Storing this in a dictionary lets `ExtendWindowUp` reuse the accurate value instead of the inaccurate 60px-per-paragraph estimate.

- [ ] **Step 1: Add the height cache field**

After the existing windowing state vars (near `_windowStart` / `_windowEnd` declarations, around line 46), add:

```csharp
// Cache accurate measured heights by 1-based chapter number.
// Populated in TrimWindowTop; used in ExtendWindowUp for accurate offset compensation.
private readonly Dictionary<int, double> _measuredChapterHeights = new();
```

- [ ] **Step 2: Populate the cache in TrimWindowTop**

In `TrimWindowTop`, find the block that computes `removedHeight`:
```csharp
var measured  = MeasureChapterHeight(chapter);
var estimated = EstimateChapterHeight(chapter);
var removedHeight = measured ?? estimated;
```
Add one line immediately after to cache the value:
```csharp
var measured  = MeasureChapterHeight(chapter);
var estimated = EstimateChapterHeight(chapter);
var removedHeight = measured ?? estimated;
_measuredChapterHeights[chapter] = removedHeight;   // ← add this
```

Note: if `MeasureChapterHeight` returned null (items not yet realized), we cache the estimate. This is still better than re-estimating on the next `ExtendWindowUp` call, because the dict value will be overwritten with an accurate measurement next time TrimTop fires for this chapter with realized items.

- [ ] **Step 3: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj --nologo -v quiet
```
Expected: `0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: cache measured chapter heights on trim

TrimWindowTop already has accurate measured height; store it in
_measuredChapterHeights so ExtendWindowUp can reuse it instead of
recomputing with the inaccurate 60px-per-paragraph estimate."
```

---

## Task 3: Use cached height in ExtendWindowUp (Bug 1, part B)

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs` — `ExtendWindowUp`

With the cache populated by Task 2, `ExtendWindowUp` can now look up an accurate scroll compensation value for any chapter that has previously been trimmed from the top.

- [ ] **Step 1: Replace the estimate call with a cache lookup**

In `ExtendWindowUp`, find:
```csharp
var estimatedHeight = EstimateChapterHeight(chapter);
```
Replace with:
```csharp
var estimatedHeight = _measuredChapterHeights.TryGetValue(chapter, out var cachedHeight)
    ? cachedHeight
    : EstimateChapterHeight(chapter);
```

- [ ] **Step 2: Update the debug log to indicate cache hit vs miss**

In `ExtendWindowUp`, find the first `Debug.WriteLine` inside `#if DEBUG`:
```csharp
Debug.WriteLine(
    $"[WIN] ExtendUp ch={chapter} paras={newParagraphs.Count} " +
    $"estimate={estimatedHeight:F1}px " +
    $"offsetBefore={offsetBefore:F1} extentBefore={extentBefore:F1} " +
    $"window=[{_windowStart}..{_windowEnd})");
```
Replace with:
```csharp
var heightSource = _measuredChapterHeights.ContainsKey(chapter) ? "cached" : "estimated";
Debug.WriteLine(
    $"[WIN] ExtendUp ch={chapter} paras={newParagraphs.Count} " +
    $"height={estimatedHeight:F1}px ({heightSource}) " +
    $"offsetBefore={offsetBefore:F1} extentBefore={extentBefore:F1} " +
    $"window=[{_windowStart}..{_windowEnd})");
```

- [ ] **Step 3: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj --nologo -v quiet
```
Expected: `0 Error(s)`

- [ ] **Step 4: Verify in debug log**

Run the app, scroll upward slowly past a chapter boundary.

Expected log pattern (after first trim+extend cycle):
```
[WIN] TrimTop ch=3 usingHeight=2603.0 ...
[WIN] ExtendUp ch=3 height=2603.0px (cached) ...
[WIN] PostLayout ch=3 estimate=2603.0 actual=2603.0 error=+0.0px ...
```

Key checks:
- `(cached)` appears instead of `(estimated)` after first cycle
- PostLayout `error` is 0.0 or very small (not 503px)
- No repeated TrimTop+ExtendUp for the same chapter at the same scrollTop

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: use cached chapter height in ExtendWindowUp for accurate offset compensation

Previously ExtendUp estimated 60px/para but actual rendered height is
~150-200px/para, causing 500-1600px scroll offset error per trim/extend
cycle. Now reuses the measured height cached by TrimWindowTop."
```

---

## Task 4: Visibility-aware trim guards to prevent Trim↔Extend oscillation (Bug 2)

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs` — `CheckWindowBounds`

**The conflict (from log):**
`CheckWindowBounds.TrimBottom` fires when `tailroom > 5×vp` and removes ch3.
`CheckWindowExtend` fires next frame and re-adds ch3 (it's `bottomVisible + 1`).
Removing ch3 changes the ScrollViewer extent, which fires `ScrollChanged`, which re-arms both checks.
Result: infinite spin at a fixed scroll position with no user input.

**The same conflict also affects TrimTop + ExtendUp** — with accurate heights from Task 3, TrimTop would become a true infinite loop (net offset change = 0 each cycle, condition never clears).

**The invariant to enforce:** `CheckWindowBounds` must never trim a chapter that `CheckWindowExtend` would immediately re-add. `CheckWindowExtend` keeps ±1 chapter around the visible range. Therefore `CheckWindowBounds` must keep at least the same ±1 buffer before trimming.

**Guard derivations:**

For TrimTop (removes chapter at `_windowStart + 1`, 1-based):
- CheckExtend re-adds if `(_windowStart_new + 1) == topVisible - 1`
- i.e., after trim `_windowStart + 1 == topVisible - 1` → conflict when `_windowStart + 1 >= topVisible - 1`
- Safe to trim when: `(_windowStart + 1) < (topVisible - 1)`

For TrimBottom (removes chapter `_windowEnd`, 1-based):
- CheckExtend re-adds if `_windowEnd == bottomVisible + 1`
- Safe to trim when: `_windowEnd > (bottomVisible + 1)`

Guards only apply when `_chapterStartY.Count > 0` (precise path). When chapter positions aren't measured, skip trim entirely (conservative — avoids oscillation during initial layout).

- [ ] **Step 1: Add TrimTop guard in CheckWindowBounds**

In `CheckWindowBounds`, find the TrimTop block:
```csharp
if (_windowEnd - _windowStart > 1 && scrollTop > vpHeight * 5)
{
#if DEBUG
    Debug.WriteLine($"[WIN] CheckBounds → TrimTop (scrollTop={scrollTop:F0} > 5×vp={vpHeight * 5:F0})");
#endif
    TrimWindowTop();
}
```
Replace with:
```csharp
if (_windowEnd - _windowStart > 1 && scrollTop > vpHeight * 5)
{
    // Guard: don't trim if CheckWindowExtend would immediately re-add the same chapter.
    // CheckExtend keeps the chapter at (topVisible - 1) loaded. Only trim chapters
    // that are strictly further above than that buffer.
    bool safeToTrimTop = false;
    if (_chapterStartY.Count > 0)
    {
        var (topVisible, _) = GetVisibleChapterRange(scrollTop, scrollBottom);
        safeToTrimTop = (_windowStart + 1) < (topVisible - 1);
    }
    // When chapter positions aren't measured yet, skip trim — can't safely decide.
    if (safeToTrimTop)
    {
#if DEBUG
        Debug.WriteLine($"[WIN] CheckBounds → TrimTop (scrollTop={scrollTop:F0} > 5×vp={vpHeight * 5:F0})");
#endif
        TrimWindowTop();
    }
#if DEBUG
    else
    {
        var (topVisible, _) = _chapterStartY.Count > 0
            ? GetVisibleChapterRange(scrollTop, scrollBottom)
            : (0, 0);
        Debug.WriteLine(
            $"[WIN] CheckBounds TrimTop SKIPPED (ch{_windowStart + 1} is within buffer of topVisible=ch{topVisible})");
    }
#endif
}
```

- [ ] **Step 2: Add TrimBottom guard in CheckWindowBounds**

In `CheckWindowBounds`, find the TrimBottom block:
```csharp
if (_windowEnd - _windowStart > 1 && contentBottom - scrollBottom > vpHeight * 5)
{
#if DEBUG
    Debug.WriteLine($"[WIN] CheckBounds → TrimBottom (tailroom={contentBottom - scrollBottom:F0} > 5×vp={vpHeight * 5:F0})");
#endif
    TrimWindowBottom();
}
```
Replace with:
```csharp
if (_windowEnd - _windowStart > 1 && contentBottom - scrollBottom > vpHeight * 5)
{
    // Guard: don't trim if CheckWindowExtend would immediately re-add the same chapter.
    // CheckExtend keeps the chapter at (bottomVisible + 1) loaded. Only trim chapters
    // that are strictly further below than that buffer.
    bool safeToTrimBottom = false;
    if (_chapterStartY.Count > 0)
    {
        var (_, bottomVisible) = GetVisibleChapterRange(scrollTop, scrollBottom);
        safeToTrimBottom = _windowEnd > (bottomVisible + 1);
    }
    // When chapter positions aren't measured yet, skip trim — can't safely decide.
    if (safeToTrimBottom)
    {
#if DEBUG
        Debug.WriteLine($"[WIN] CheckBounds → TrimBottom (tailroom={contentBottom - scrollBottom:F0} > 5×vp={vpHeight * 5:F0})");
#endif
        TrimWindowBottom();
    }
#if DEBUG
    else
    {
        var (_, bottomVisible) = _chapterStartY.Count > 0
            ? GetVisibleChapterRange(scrollTop, scrollBottom)
            : (0, 0);
        Debug.WriteLine(
            $"[WIN] CheckBounds TrimBottom SKIPPED (ch{_windowEnd} is within buffer of bottomVisible=ch{bottomVisible})");
    }
#endif
}
```

- [ ] **Step 3: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj --nologo -v quiet
```
Expected: `0 Error(s)`

- [ ] **Step 4: Verify — no infinite loops**

Run the app, scroll slowly upward. In the debug log, look for the infinite spin pattern:
```
[WIN] CheckBounds → TrimBottom ...
[WIN] CheckExtend → ExtendDown ...
[WIN] CheckBounds → TrimBottom ...
[WIN] CheckExtend → ExtendDown ...
```
This must NOT appear. Instead, expect:
```
[WIN] CheckBounds TrimBottom SKIPPED (ch3 is within buffer of bottomVisible=ch2)
```
And scrollTop should continue changing (user can scroll) rather than being stuck at a fixed value.

Also scroll past a chapter boundary going backward:
```
[WIN] CheckBounds TrimTop SKIPPED (ch3 is within buffer of topVisible=ch4)
```
And no TrimTop+ExtendUp oscillation at the same scrollTop.

- [ ] **Step 5: Verify window doesn't grow unboundedly**

Scroll forward through 10+ chapters, then back. Window size should remain bounded. Log should show TrimBottom and TrimTop firing occasionally for chapters that are genuinely far from the visible range (> ±1 buffer).

Example of a legitimate trim that SHOULD still fire:
```
[WIN] CheckBounds → TrimBottom (tailroom=8000 > 5×vp=2835)
```
where the trimmed chapter is `ch7` and `bottomVisible=ch4` → `_windowEnd=7 > 4+1=5` → trim fires.

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: add visibility-aware guards to prevent Trim/Extend oscillation loop

CheckWindowBounds would trim a chapter that CheckWindowExtend immediately
re-added, causing an infinite spin loop (observed 40+ cycles at fixed
scroll position). Guards now skip trim when the target chapter is the
±1 buffer chapter that CheckExtend maintains around the visible range.

Affects both TrimTop (was causing jump oscillation) and TrimBottom
(was causing CPU-spinning infinite loop)."
```

---

## Self-Review

**Spec coverage:**
- Bug 1 (TrimTop/ExtendUp jump from estimate error) → Tasks 2 + 3 + guard in Task 4 ✓
- Bug 2 (TrimBottom/ExtendDown infinite spin) → Task 4 TrimBottom guard ✓
- Bug 3 (format string shows "+F1" literal) → Task 1 ✓

**Placeholder scan:** No TBD, no "similar to", no "add appropriate" phrases. All code blocks complete. ✓

**Type consistency:**
- `_measuredChapterHeights: Dictionary<int, double>` — introduced Task 2, used Task 3 ✓
- `GetVisibleChapterRange(scrollTop, scrollBottom)` — existing method, used same signature in Task 4 ✓
- `(_windowStart + 1)` vs `_windowEnd` chapter number conventions — consistent with existing `TrimWindowTop`/`TrimWindowBottom` logic ✓

**Edge cases covered:**
- First-time chapter load (never trimmed before) → falls back to `EstimateChapterHeight` in Task 3 ✓
- `_chapterStartY` not yet populated → trim guards skip trim conservatively ✓
- `MeasureChapterHeight` returns null in TrimTop → caches estimate, will be overwritten on next trim ✓
