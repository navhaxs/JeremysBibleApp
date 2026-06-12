# Battery Drain Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the auto-sync loop and network monitor when the app is backgrounded (Android) or minimized (Desktop), reducing background battery drain from ~720 network wake-ups/day to near zero.

**Architecture:** New `AppLifecycleService` singleton fires `Suspended`/`Resumed` events. `SharedSyncRuntime` wires stop/start of sync and network monitor on these events. `AppViewModel` wires stop/start of the UI timer. Android `MainActivity` and Desktop `AppShellView` call `Suspend()`/`Resume()` at the right lifecycle moments. Sync interval also increased from 2 → 15 minutes.

**Tech Stack:** C#, Avalonia, Android Activity lifecycle (`OnPause`/`OnResume`), Avalonia `Window.PropertyChanged` for `WindowState`

---

## File Structure

| File | Change |
|---|---|
| `MyBibleApp/Services/AppLifecycleService.cs` | **New** — event source, singleton |
| `MyBibleApp/Services/SharedSyncRuntime.cs` | Wire lifecycle events; change `AutoSyncIntervalMinutes` 2→15 |
| `MyBibleApp.Android/MainActivity.cs` | Add `OnPause`/`OnResume` overrides |
| `MyBibleApp/Views/AppShellView.axaml.cs` | Subscribe to `Window.WindowStateProperty` changes |
| `MyBibleApp/ViewModels/AppViewModel.cs` | Subscribe to lifecycle events for UI timer |

---

### Task 1: Create `AppLifecycleService`

**Files:**
- Create: `MyBibleApp/Services/AppLifecycleService.cs`

- [ ] **Step 1: Create the file**

```csharp
using System;

namespace MyBibleApp.Services;

internal sealed class AppLifecycleService
{
    private static readonly Lazy<AppLifecycleService> SharedInstance = new(() => new());
    public static AppLifecycleService Instance => SharedInstance.Value;

    public event EventHandler? Suspended;
    public event EventHandler? Resumed;

    private bool _suspended;

    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        Suspended?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        Resumed?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 2: Build to verify**

```powershell
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add MyBibleApp/Services/AppLifecycleService.cs
git commit -m "feat: add AppLifecycleService for cross-platform suspend/resume"
```

---

### Task 2: Wire lifecycle into `SharedSyncRuntime` and increase sync interval

**Files:**
- Modify: `MyBibleApp/Services/SharedSyncRuntime.cs`

Context: `SharedSyncRuntime` is a singleton factory (`Create()` runs once). `SyncCoordinator.StartAutoSync()` already exists at line 65. `NetworkStatusMonitor.StopMonitoring()`/`StartMonitoring()` already exist. The `Suspended` event handler stops both; `Resumed` restarts both. The existing `StartAutoSync()` call at line 65 stays — it handles cold start (the `Resumed` event only fires on transition from suspended, not on first launch).

- [ ] **Step 1: Change `AutoSyncIntervalMinutes` from 2 to 15**

In `MyBibleApp/Services/SharedSyncRuntime.cs`, line 10:

```csharp
// Before:
private const int AutoSyncIntervalMinutes = 2;

// After:
private const int AutoSyncIntervalMinutes = 15;
```

- [ ] **Step 2: Wire lifecycle events in `Create()`**

In `Create()`, after `syncCoordinator.SetJournalSyncProvider(journalSyncProvider)` (line 63) and before `syncCoordinator.StartAutoSync(...)` (line 65), add:

```csharp
var lifecycle = AppLifecycleService.Instance;
lifecycle.Suspended += (_, _) =>
{
    syncCoordinator.StopAutoSync();
    networkMonitor.StopMonitoring();
};
lifecycle.Resumed += (_, _) =>
{
    networkMonitor.StartMonitoring();
    syncCoordinator.StartAutoSync(TimeSpan.FromMinutes(AutoSyncIntervalMinutes));
};
```

The full `Create()` method body after your changes (lines 48–68) should look like:

```csharp
private static SharedSyncRuntime Create()
{
    IGoogleDriveAuthService authService = PlatformHelper.IsAndroid
        ? new AndroidGoogleDriveAuthService()
        : new DesktopGoogleDriveAuthService(
            () => AssetLoader.Open(new Uri("avares://MyBibleApp/Assets/credentials.desktop.json")));

    var syncService = new GoogleDriveSyncService(authService);
    var queueManager = new FileSyncQueueManager();
    var networkMonitor = new NetworkStatusMonitor();
    var localStorage = new FileBasedLocalStorageProvider();
    var syncCoordinator = new SyncCoordinator(authService, syncService, queueManager, networkMonitor, localStorage);

    var journalStore = new JournalStore();
    var journalSyncProvider = new JournalSyncProviderAdapter(journalStore);
    syncCoordinator.SetJournalSyncProvider(journalSyncProvider);

    var lifecycle = AppLifecycleService.Instance;
    lifecycle.Suspended += (_, _) =>
    {
        syncCoordinator.StopAutoSync();
        networkMonitor.StopMonitoring();
    };
    lifecycle.Resumed += (_, _) =>
    {
        networkMonitor.StartMonitoring();
        syncCoordinator.StartAutoSync(TimeSpan.FromMinutes(AutoSyncIntervalMinutes));
    };

    syncCoordinator.StartAutoSync(TimeSpan.FromMinutes(AutoSyncIntervalMinutes));

    return new SharedSyncRuntime(authService, syncService, queueManager, networkMonitor, localStorage, syncCoordinator, journalStore);
}
```

- [ ] **Step 3: Build to verify**

```powershell
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Services/SharedSyncRuntime.cs
git commit -m "feat: wire lifecycle suspend/resume into sync coordinator and increase interval to 15 min"
```

---

### Task 3: Android `OnPause` / `OnResume` lifecycle hooks

**Files:**
- Modify: `MyBibleApp.Android/MainActivity.cs`

Context: `MainActivity` extends `AvaloniaMainActivity`. `OnPause` fires when Android moves the app to the background (home button, app switcher, incoming call, etc.). `OnResume` fires when the app returns to foreground. These are the canonical Android backgrounding signals.

- [ ] **Step 1: Add `using MyBibleApp.Services;` to imports**

In `MyBibleApp.Android/MainActivity.cs`, the current imports are:
```csharp
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using MyBibleApp.Services.Sync;
```

Change to:
```csharp
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using MyBibleApp.Services;
using MyBibleApp.Services.Sync;
```

- [ ] **Step 2: Add `OnPause` and `OnResume` overrides**

Inside the `MainActivity` class, after `OnNewIntent` (currently the last method, ending around line 61), add:

```csharp
protected override void OnPause()
{
    base.OnPause();
    AppLifecycleService.Instance.Suspend();
}

protected override void OnResume()
{
    base.OnResume();
    AppLifecycleService.Instance.Resume();
}
```

- [ ] **Step 3: Build to verify**

```powershell
dotnet build MyBibleApp.Android/MyBibleApp.Android.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp.Android/MainActivity.cs
git commit -m "feat: suspend/resume sync on Android app background/foreground"
```

---

### Task 4: Desktop window minimize / restore detection

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs`

Context: `AppShellView` is a `UserControl`. Its `VisualRoot` (the parent `Window`) is not available in the constructor — only after the control is attached to the visual tree. Use `AttachedToVisualTree` to hook up the window state listener. `Window.WindowStateProperty` is an `AvaloniaProperty`; changes come through `PropertyChanged` with `e.Property == Window.WindowStateProperty`. Suspend when entering `WindowState.Minimized`; resume when leaving it.

- [ ] **Step 1: Add `AttachedToVisualTree` subscription in constructor**

In `AppShellView.axaml.cs`, in the constructor, after the existing `this.SizeChanged += OnShellSizeChanged;` line (around line 143), add:

```csharp
AttachedToVisualTree += OnAttachedToVisualTree;
```

- [ ] **Step 2: Add `OnAttachedToVisualTree` and `OnWindowPropertyChanged` handlers**

Add these two methods anywhere in `AppShellView.axaml.cs` (e.g., near the bottom before the closing brace):

```csharp
private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
{
    if (this.VisualRoot is Window window)
        window.PropertyChanged += OnWindowPropertyChanged;
}

private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
{
    if (e.Property != Window.WindowStateProperty) return;
    var newState = e.NewValue as WindowState?;
    var oldState = e.OldValue as WindowState?;
    if (newState == WindowState.Minimized)
        AppLifecycleService.Instance.Suspend();
    else if (oldState == WindowState.Minimized)
        AppLifecycleService.Instance.Resume();
}
```

`VisualTreeAttachmentEventArgs` is in `Avalonia.VisualTree` — already imported (line 18 of `AppShellView.axaml.cs`). `Window` and `WindowState` are in `Avalonia.Controls` — already imported.

- [ ] **Step 3: Build to verify**

```powershell
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/AppShellView.axaml.cs
git commit -m "feat: suspend/resume sync when desktop window is minimized/restored"
```

---

### Task 5: Stop UI timer when app is suspended

**Files:**
- Modify: `MyBibleApp/ViewModels/AppViewModel.cs`

Context: `AppViewModel` already has `StartSyncStatusTimer()` (line 740) and `StopSyncStatusTimer()` (line 748). The timer runs every 30 seconds to update the sync status UI string. It should stop when the app is backgrounded and restart on foreground (only if authenticated, matching existing call-site logic at line 718).

- [ ] **Step 1: Add lifecycle subscription to `AppViewModel` constructor**

In `AppViewModel.cs`, at the end of the `try` block in the constructor (after line 61, `AppendSyncDebugLog("Sync services initialized.")`), add:

```csharp
var lifecycle = AppLifecycleService.Instance;
lifecycle.Suspended += (_, _) => StopSyncStatusTimer();
lifecycle.Resumed += (_, _) =>
{
    if (IsAuthenticated)
        StartSyncStatusTimer();
};
```

The full constructor `try` block should look like:

```csharp
try
{
    var sharedSyncRuntime = SharedSyncRuntime.Instance;

    _googleDriveAuthService = sharedSyncRuntime.GoogleDriveAuthService;
    _googleDriveSyncService = sharedSyncRuntime.GoogleDriveSyncService;
    _syncQueueManager = sharedSyncRuntime.SyncQueueManager;
    _localStorageProvider = sharedSyncRuntime.LocalStorageProvider;
    _syncCoordinator = sharedSyncRuntime.SyncCoordinator;

    _syncCoordinator.SyncProgress += OnSyncProgress;
    _googleDriveAuthService.AuthStateChanged += OnAuthStateChanged;
    IsAuthenticated = _googleDriveAuthService.IsAuthenticated;
    CurrentUserEmail = _googleDriveAuthService.CurrentUserEmail;

    SyncStatus = "Sync initialized";
    AppendSyncDebugLog("Sync services initialized.");

    var lifecycle = AppLifecycleService.Instance;
    lifecycle.Suspended += (_, _) => StopSyncStatusTimer();
    lifecycle.Resumed += (_, _) =>
    {
        if (IsAuthenticated)
            StartSyncStatusTimer();
    };
}
```

- [ ] **Step 2: Build to verify**

```powershell
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Run all tests**

```powershell
dotnet test
```

Expected: 154 tests pass, 0 failures.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/ViewModels/AppViewModel.cs
git commit -m "feat: stop sync status UI timer when app is suspended"
```

---

## Manual Verification Checklist

After all tasks complete:

| Platform | Action | Expected |
|---|---|---|
| Android | Press home button | Sync stops (no network activity in background) |
| Android | Return to app | Sync resumes within 15 min |
| Android | Lock screen | Sync stops |
| Android | Unlock screen | Sync resumes |
| Desktop | Minimize window | Sync pauses |
| Desktop | Restore window | Sync resumes |
| Both | Double-press home/minimize | No crash, events fire once (idempotent guard) |
| Both | Background while offline | No crash (sync was already idle) |
