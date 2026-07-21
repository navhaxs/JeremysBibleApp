# Mobile Rotation: Per-Orientation Journal Pan Memory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remember the journal ink horizontal pan offset separately for portrait and landscape on mobile, so rotating the device restores the alignment that orientation had before.

**Architecture:** Two pure/testable units (`OrientationPanHelper` for the width/height → portrait math and clamp math, `UiPreferencesStore` for JSON-file persistence) get TDD'd in isolation, then wired into `MainView.axaml.cs` as thin glue: a root `SizeChanged` hook detects orientation flips and flags a pending restore, the existing `_inkAreaGrid.SizeChanged` handler applies the restore once `Extent`/`Viewport` are valid, and the two existing pan-writing call sites also capture+persist the live offset.

**Tech Stack:** .NET 10, Avalonia, xUnit (existing `MyBibleApp.Journal.Tests` project).

## Global Constraints

- Mobile only — gate all new behavior on `!PlatformHelper.IsDesktop` (`MyBibleApp/Services/PlatformHelper.cs`). Desktop resize must be untouched.
- One global pan value per orientation for the whole app (not per journal/chapter). Because `MainView` can have two live instances (split view — see `_isSecondaryPane` field), the shared state must be `static`, not instance-level, so both panes converge on the same app-wide value.
- Persisted to disk at `%AppData%/MyBibleApp/ui-prefs.json`, atomic write (temp file + `File.Move(overwrite: true)`), following the exact pattern already used in `MyBibleApp/Services/JournalStore.cs:637-646` (`WriteAtomically`) and `:648-652` (`GetDefaultStoragePath`).
- Restore math: raw pixel offset, clamped to the new orientation's max scroll range — `Math.Clamp(stored, 0, Math.Max(0, extentWidth - viewportWidth))`, matching the existing pattern at `MainView.axaml.cs:2614-2616` and `:2748-2752`.
- Spec: [2026-07-21-mobile-rotation-pan-memory-design.md](../specs/2026-07-21-mobile-rotation-pan-memory-design.md)

---

### Task 1: `OrientationPanHelper` — pure orientation/clamp math

**Files:**
- Create: `MyBibleApp/Helpers/OrientationPanHelper.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/OrientationPanHelperTests.cs`

**Interfaces:**
- Produces: `MyBibleApp.Helpers.OrientationPanHelper.IsPortrait(double width, double height) : bool` and `MyBibleApp.Helpers.OrientationPanHelper.ClampPanX(double stored, double extentWidth, double viewportWidth) : double`. Task 3 and Task 4 call these directly.

- [ ] **Step 1: Write the failing tests**

```csharp
using MyBibleApp.Helpers;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class OrientationPanHelperTests
{
    [Theory]
    [InlineData(400, 800, true)]   // taller than wide -> portrait
    [InlineData(800, 400, false)]  // wider than tall -> landscape
    [InlineData(500, 500, true)]   // square -> treat as portrait
    public void IsPortrait_ClassifiesFromWidthAndHeight(double width, double height, bool expected)
    {
        Assert.Equal(expected, OrientationPanHelper.IsPortrait(width, height));
    }

    [Fact]
    public void ClampPanX_WithinRange_ReturnsStoredValue()
    {
        var result = OrientationPanHelper.ClampPanX(stored: 100, extentWidth: 1000, viewportWidth: 400);
        Assert.Equal(100, result);
    }

    [Fact]
    public void ClampPanX_ExceedsNewMax_ClampsDown()
    {
        // maxX = 1000 - 400 = 600, stored 800 should clamp to 600
        var result = OrientationPanHelper.ClampPanX(stored: 800, extentWidth: 1000, viewportWidth: 400);
        Assert.Equal(600, result);
    }

    [Fact]
    public void ClampPanX_Negative_ClampsToZero()
    {
        var result = OrientationPanHelper.ClampPanX(stored: -50, extentWidth: 1000, viewportWidth: 400);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ClampPanX_ViewportWiderThanExtent_ReturnsZero()
    {
        // maxX = max(0, 400 - 1000) = 0
        var result = OrientationPanHelper.ClampPanX(stored: 300, extentWidth: 400, viewportWidth: 1000);
        Assert.Equal(0, result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter OrientationPanHelperTests`
Expected: FAIL to compile — `OrientationPanHelper` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System;

namespace MyBibleApp.Helpers;

/// <summary>
/// Pure math for tracking journal ink pan offset across device rotation:
/// classifying portrait/landscape from bounds, and clamping a remembered
/// offset to fit the new orientation's scroll range.
/// </summary>
public static class OrientationPanHelper
{
    public static bool IsPortrait(double width, double height) => height >= width;

    public static double ClampPanX(double stored, double extentWidth, double viewportWidth)
    {
        var maxX = Math.Max(0, extentWidth - viewportWidth);
        return Math.Clamp(stored, 0, maxX);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter OrientationPanHelperTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Helpers/OrientationPanHelper.cs MyBibleApp.Journal.Tests/Unit/OrientationPanHelperTests.cs
git commit -m "feat: add OrientationPanHelper for rotation pan-memory math"
```

---

### Task 2: `UiPreferencesStore` — JSON-file-backed pan persistence

**Files:**
- Create: `MyBibleApp/Services/UiPreferencesStore.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/UiPreferencesStoreTests.cs`

**Interfaces:**
- Consumes: nothing new (uses `System.Text.Json`, `System.IO`, matching `JournalStore`'s existing conventions).
- Produces: `MyBibleApp.Services.UiPreferencesStore` with:
  - `UiPreferencesStore(string? storagePath = null)` constructor (storagePath overrides default `%AppData%/MyBibleApp` dir — same shape as `JournalStore(string? storagePath = null)`)
  - `Task<(double PortraitX, double LandscapeX)> LoadJournalPanAsync()`
  - `Task SaveJournalPanAsync(double portraitX, double landscapeX)`

  Task 3 calls both of these on the static instance it creates.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using MyBibleApp.Services;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class UiPreferencesStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ui_prefs_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task LoadJournalPanAsync_NoFileYet_ReturnsZeros()
    {
        var store = new UiPreferencesStore(_tempDir);
        var (portraitX, landscapeX) = await store.LoadJournalPanAsync();
        Assert.Equal(0, portraitX);
        Assert.Equal(0, landscapeX);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsValues()
    {
        var store = new UiPreferencesStore(_tempDir);
        await store.SaveJournalPanAsync(123.5, 678.25);

        var reloaded = new UiPreferencesStore(_tempDir);
        var (portraitX, landscapeX) = await reloaded.LoadJournalPanAsync();

        Assert.Equal(123.5, portraitX);
        Assert.Equal(678.25, landscapeX);
    }

    [Fact]
    public async Task LoadJournalPanAsync_CorruptFile_ReturnsZerosInsteadOfThrowing()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "ui-prefs.json"), "{ not valid json");

        var store = new UiPreferencesStore(_tempDir);
        var (portraitX, landscapeX) = await store.LoadJournalPanAsync();

        Assert.Equal(0, portraitX);
        Assert.Equal(0, landscapeX);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter UiPreferencesStoreTests`
Expected: FAIL to compile — `UiPreferencesStore` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyBibleApp.Services;

public sealed class UiPreferencesStore
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public UiPreferencesStore(string? storagePath = null)
    {
        var storageDir = storagePath ?? GetDefaultStoragePath();
        _filePath = Path.Combine(storageDir, "ui-prefs.json");
    }

    public async Task<(double PortraitX, double LandscapeX)> LoadJournalPanAsync()
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(_filePath)) return (0.0, 0.0);
            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<UiPreferencesData>(json, JsonOptions);
                return (data?.PortraitJournalPanX ?? 0.0, data?.LandscapeJournalPanX ?? 0.0);
            }
            catch
            {
                return (0.0, 0.0);
            }
        }).ConfigureAwait(false);
    }

    public async Task SaveJournalPanAsync(double portraitX, double landscapeX)
    {
        await Task.Run(() =>
        {
            var data = new UiPreferencesData
            {
                PortraitJournalPanX = portraitX,
                LandscapeJournalPanX = landscapeX
            };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            WriteAtomically(_filePath, json);
        }).ConfigureAwait(false);
    }

    private static void WriteAtomically(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempFilePath, content);
        File.Move(tempFilePath, filePath, overwrite: true);
    }

    private static string GetDefaultStoragePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MyBibleApp");
    }

    private sealed class UiPreferencesData
    {
        public double PortraitJournalPanX { get; set; }
        public double LandscapeJournalPanX { get; set; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter UiPreferencesStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Services/UiPreferencesStore.cs MyBibleApp.Journal.Tests/Unit/UiPreferencesStoreTests.cs
git commit -m "feat: add UiPreferencesStore for persisted journal pan offsets"
```

---

### Task 3: Wire static state + startup load in `MainView.axaml.cs`

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs:146` (field block, right after `_journalHScrollNeedsReset`)
- Modify: `MyBibleApp/Views/MainView.axaml.cs:243-269` (`OnLoaded`, right after `_contentHScrollContainer` is wired at line 268)

**Interfaces:**
- Consumes: `OrientationPanHelper.IsPortrait` (Task 1), `UiPreferencesStore` (Task 2).
- Produces: static fields `_uiPrefsStore`, `_portraitPanX`, `_landscapePanX`, `_lastOrientationIsPortrait`, `_pendingOrientationRestore` — Task 4 and Task 5 read/write these.

- [ ] **Step 1: Add static fields**

In `MyBibleApp/Views/MainView.axaml.cs`, immediately after line 146 (`private bool _journalHScrollNeedsReset;`), add:

```csharp
    private static readonly UiPreferencesStore _uiPrefsStore = new();
    private static bool _uiPrefsLoadStarted;
    private static double _portraitPanX;
    private static double _landscapePanX;
    private static bool? _lastOrientationIsPortrait; // null = not yet determined
    private static bool _pendingOrientationRestore;  // set on orientation flip, cleared once restore is applied
```

- [ ] **Step 2: Load persisted values once, at startup**

In `OnLoaded`, immediately after the `_contentHScrollContainer = this.FindControl<ScrollViewer>("ContentHScrollContainer");` line (line 268), add:

```csharp
        if (!PlatformHelper.IsDesktop && !_uiPrefsLoadStarted)
        {
            _uiPrefsLoadStarted = true;
            _ = LoadUiPrefsAsync();
        }
```

Then add this new method near the end of the "Journal integration" region (right before the closing `}` of the class, after `UpdateInkTextColumnOffset`, i.e. after line 2883's closing method body):

```csharp
    private static async Task LoadUiPrefsAsync()
    {
        var (portraitX, landscapeX) = await _uiPrefsStore.LoadJournalPanAsync();
        _portraitPanX = portraitX;
        _landscapePanX = landscapeX;
    }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build MyBibleApp.Desktop/MyBibleApp.Desktop.csproj`
Expected: Build succeeded, no errors.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: load persisted journal pan offsets on startup (mobile only)"
```

---

### Task 4: Detect orientation flips and restore on rotation

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs:243-269` (`OnLoaded`, hook root `SizeChanged`)
- Modify: `MyBibleApp/Views/MainView.axaml.cs:403-417` (`_inkAreaGrid.SizeChanged` handler — add restore branch)

**Interfaces:**
- Consumes: `OrientationPanHelper.IsPortrait`, `OrientationPanHelper.ClampPanX` (Task 1); static fields from Task 3.
- Produces: `OnRootSizeChanged(object?, SizeChangedEventArgs)` method — no other task depends on its name, it's only wired as an event handler.

- [ ] **Step 1: Hook root `SizeChanged` in `OnLoaded`**

Immediately after the block added in Task 3 Step 2 (the `_uiPrefsLoadStarted` check), add:

```csharp
        if (!PlatformHelper.IsDesktop)
            this.SizeChanged += OnRootSizeChanged;
```

- [ ] **Step 2: Add the orientation-change handler**

Add this method right after `OnLoaded` closes (after line 444's closing `}`, before `RemoveHScrollContainerRecognizer`):

```csharp
    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var isPortrait = OrientationPanHelper.IsPortrait(e.NewSize.Width, e.NewSize.Height);
        if (_lastOrientationIsPortrait.HasValue && _lastOrientationIsPortrait.Value != isPortrait)
            _pendingOrientationRestore = true;
        _lastOrientationIsPortrait = isPortrait;
    }
```

- [ ] **Step 3: Apply the restore in the existing `_inkAreaGrid.SizeChanged` handler**

In the `_inkAreaGrid.SizeChanged` handler (lines 403-417), the existing body is:

```csharp
            _inkAreaGrid.SizeChanged += (_, _) =>
            {
                UpdateInkTextColumnOffset();
                HScrollDiagLog($"InkAreaGrid.SizeChanged newSize={_inkAreaGrid.Bounds.Size} " +
                    $"needsReset={_journalHScrollNeedsReset} " +
                    $"extent={_contentHScrollContainer?.Extent} viewport={_contentHScrollContainer?.Viewport} " +
                    $"homePanX={_journalHomePanX:F1} hScrollLocked={_hScrollLocked}");
                if (_journalHScrollNeedsReset && _contentHScrollContainer != null
                    && _contentHScrollContainer.Extent.Width > _contentHScrollContainer.Viewport.Width)
                {
                    _contentHScrollContainer.Offset = new Vector(_journalHomePanX, 0);
                    _journalHScrollNeedsReset = false;
                    HScrollDiagLog("Home pan applied, needsReset cleared.");
                }
            };
```

Add a second `if` block right after the existing one (still inside the same lambda, after the closing `}` of the `_journalHScrollNeedsReset` block, before the lambda's own closing `};`):

```csharp
                if (_pendingOrientationRestore && _contentHScrollContainer != null
                    && _journalHomePanX > 0
                    && _contentHScrollContainer.Extent.Width > _contentHScrollContainer.Viewport.Width)
                {
                    var target = _lastOrientationIsPortrait == true ? _portraitPanX : _landscapePanX;
                    var clamped = OrientationPanHelper.ClampPanX(target,
                        _contentHScrollContainer.Extent.Width, _contentHScrollContainer.Viewport.Width);
                    _contentHScrollContainer.Offset = new Vector(clamped, 0);
                    _pendingOrientationRestore = false;
                    HScrollDiagLog($"Orientation restore applied: isPortrait={_lastOrientationIsPortrait} target={target:F1} clamped={clamped:F1}");
                }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build MyBibleApp.Desktop/MyBibleApp.Desktop.csproj`
Expected: Build succeeded, no errors.

- [ ] **Step 5: Manual test on Android (device/emulator)**

1. Deploy to a device: `deploy-s24.bat` (or the emulator/device deploy script that matches available hardware).
2. Open a chapter with an active journal wide enough to trigger h-scroll (annotation toggle on, `TextColumnWidthDip` layout wider than viewport).
3. In portrait, pan the ink area horizontally to a distinctive position.
4. Rotate to landscape, pan to a different distinctive position.
5. Rotate back to portrait — confirm the portrait pan position from step 3 is restored (not the landscape one, not reset to home).
6. Rotate to landscape again — confirm the landscape pan position from step 4 is restored.
7. Kill and relaunch the app, repeat step 5/6 — confirm values survived the relaunch (persistence).

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: restore per-orientation journal pan offset on device rotation"
```

---

### Task 5: Capture and persist pan offset on every user pan

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs:2612-2617` (`OnMarginTouchMoved`, horizontal-pan branch)
- Modify: `MyBibleApp/Views/MainView.axaml.cs:2746-2754` (`OnHorizontalWheelChanged`)

**Interfaces:**
- Consumes: static fields from Task 3, `UiPreferencesStore.SaveJournalPanAsync` (Task 2).
- Produces: `CaptureAndPersistPan(double newX)` — private helper, no other task depends on it.

- [ ] **Step 1: Add the capture helper**

Add this method next to `OnRootSizeChanged` (added in Task 4 Step 2):

```csharp
    private static void CaptureAndPersistPan(double newX)
    {
        // _lastOrientationIsPortrait is only non-null on mobile (Task 4 only hooks
        // OnRootSizeChanged when !PlatformHelper.IsDesktop), so this is a no-op on desktop.
        if (_lastOrientationIsPortrait == true) _portraitPanX = newX;
        else if (_lastOrientationIsPortrait == false) _landscapePanX = newX;
        else return;

        _ = _uiPrefsStore.SaveJournalPanAsync(_portraitPanX, _landscapePanX);
    }
```

- [ ] **Step 2: Call it from the touch-drag pan site**

In `OnMarginTouchMoved`, the existing block at lines 2612-2617 is:

```csharp
        if (_touchPanAxis == PanAxis.Horizontal && _contentHScrollContainer != null)
        {
            var maxX = Math.Max(0, _contentHScrollContainer.Extent.Width - _contentHScrollContainer.Viewport.Width);
            var newX = Math.Clamp(_contentHScrollContainer.Offset.X + deltaX, 0, maxX);
            _contentHScrollContainer.Offset = new Vector(newX, _contentHScrollContainer.Offset.Y);
        }
```

Add a call to the new helper right after the `Offset` assignment:

```csharp
        if (_touchPanAxis == PanAxis.Horizontal && _contentHScrollContainer != null)
        {
            var maxX = Math.Max(0, _contentHScrollContainer.Extent.Width - _contentHScrollContainer.Viewport.Width);
            var newX = Math.Clamp(_contentHScrollContainer.Offset.X + deltaX, 0, maxX);
            _contentHScrollContainer.Offset = new Vector(newX, _contentHScrollContainer.Offset.Y);
            CaptureAndPersistPan(newX);
        }
```

- [ ] **Step 3: Call it from the wheel-pan site**

In `OnHorizontalWheelChanged` (lines 2746-2754), the existing body is:

```csharp
    private void OnHorizontalWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.X == 0 || _contentHScrollContainer == null || _hScrollLocked) return;
        const double ScrollStep = 50.0;
        var maxX = Math.Max(0, _contentHScrollContainer.Extent.Width - _contentHScrollContainer.Viewport.Width);
        var newX = Math.Clamp(_contentHScrollContainer.Offset.X - e.Delta.X * ScrollStep, 0, maxX);
        _contentHScrollContainer.Offset = new Vector(newX, _contentHScrollContainer.Offset.Y);
        e.Handled = true;
    }
```

Add the same call right after the `Offset` assignment:

```csharp
    private void OnHorizontalWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.X == 0 || _contentHScrollContainer == null || _hScrollLocked) return;
        const double ScrollStep = 50.0;
        var maxX = Math.Max(0, _contentHScrollContainer.Extent.Width - _contentHScrollContainer.Viewport.Width);
        var newX = Math.Clamp(_contentHScrollContainer.Offset.X - e.Delta.X * ScrollStep, 0, maxX);
        _contentHScrollContainer.Offset = new Vector(newX, _contentHScrollContainer.Offset.Y);
        CaptureAndPersistPan(newX);
        e.Handled = true;
    }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build MyBibleApp.Desktop/MyBibleApp.Desktop.csproj`
Expected: Build succeeded, no errors.

- [ ] **Step 5: Manual re-test on Android**

Repeat the manual test from Task 4 Step 5 end-to-end (this is the step that makes the persisted values actually reflect live user panning instead of only the home position) — confirm portrait/landscape positions are independently remembered and restored correctly across rotations and app relaunch.

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: persist journal pan offset on every user pan gesture"
```

---

## Self-Review Notes

- **Spec coverage:** orientation detection (Task 4), live capture (Task 5), persistence (Task 2), restore-clamped (Task 4/1), mobile-only gating (Tasks 3-5, via `PlatformHelper.IsDesktop` and the null-orientation no-op in `CaptureAndPersistPan`), global-not-per-journal scope (static fields, Task 3) — all covered.
- **Type consistency:** `UiPreferencesStore.LoadJournalPanAsync()` returns `(double PortraitX, double LandscapeX)` consistently in Task 2 and Task 3; `OrientationPanHelper.IsPortrait`/`ClampPanX` signatures match between Task 1's definition and Tasks 4/5's call sites.
- **No placeholders:** every step has complete code, exact file paths/line numbers, and exact build/test commands.
