# Battery Drain Fix Design

**Date:** 2026-06-12
**Status:** Approved
**Scope:** 1 new file, 4 modified files

## Problem

`MyBibleApp` consumes 58% battery in background over 1 day on Android because:
- Auto-sync loop runs every 2 minutes with no pause on background — ~720 network radio wake-ups/day
- Network monitor never stops when app is backgrounded
- No Android `OnPause`/`OnResume` lifecycle hooks exist
- 30-sec UI status timer runs indefinitely

## Architecture

New `AppLifecycleService` singleton acts as a thin event source. Platforms call `Suspend()`/`Resume()`. Consumers subscribe independently.

```
Android OnPause/OnResume  ──┐
Desktop window minimize   ──┤──► AppLifecycleService ──► Suspended / Resumed events
                             │                               │               │
                             └───────────────────────────────▼───────────────▼
                                                   SharedSyncRuntime    AppViewModel
                                               (sync + network monitor)  (UI timer)
```

## Components

### 1. New: `MyBibleApp/Services/AppLifecycleService.cs`

Single-responsibility: broadcast app lifecycle state. No business logic.

```csharp
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

Idempotent: double `Suspend()` or double `Resume()` fires the event only once.

---

### 2. Modified: `MyBibleApp/Services/SharedSyncRuntime.cs`

**Change `AutoSyncIntervalMinutes`:** 2 → 15

**Wire lifecycle in `Create()`** after constructing `syncCoordinator`:

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

The existing `syncCoordinator.StartAutoSync(...)` call at line 65 **remains** — it handles cold start. The lifecycle events only handle subsequent suspend/resume transitions.

---

### 3. Modified: `MyBibleApp.Android/MainActivity.cs`

Override `OnPause` and `OnResume`:

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

Requires `using MyBibleApp.Services;`.

---

### 4. Modified: `MyBibleApp/Views/AppShellView.axaml.cs`

In `OnLoaded`, get the parent `Window` and subscribe to `WindowState` changes:

```csharp
if (this.VisualRoot is Window window)
{
    window.PropertyChanged += OnWindowPropertyChanged;
}
```

Handler:

```csharp
private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
{
    if (e.Property != Window.WindowStateProperty) return;
    if (e.NewValue is WindowState.Minimized)
        AppLifecycleService.Instance.Suspend();
    else if (e.OldValue is WindowState.Minimized)
        AppLifecycleService.Instance.Resume();
}
```

Only fires on `WindowState` changes. Suspend on minimize, resume when leaving minimized state.

---

### 5. Modified: `MyBibleApp/ViewModels/AppViewModel.cs`

In the constructor, after `SharedSyncRuntime.Instance` is accessed, subscribe to lifecycle:

```csharp
var lifecycle = AppLifecycleService.Instance;
lifecycle.Suspended += (_, _) => StopSyncStatusTimer();
lifecycle.Resumed += (_, _) =>
{
    if (IsAuthenticated)
        StartSyncStatusTimer();
};
```

`StartSyncStatusTimer()` and `StopSyncStatusTimer()` already exist (lines 740–752).

---

## Data Flow

1. Android home button → `OnPause()` → `AppLifecycleService.Suspend()`
2. Event fires → `SharedSyncRuntime` stops auto-sync loop + network monitor
3. Event fires → `AppViewModel` stops UI timer
4. App foregrounded → `OnResume()` → `AppLifecycleService.Resume()`
5. Event fires → network monitor restarted → auto-sync restarted (15-min interval)
6. Event fires → UI timer restarted (if authenticated)

---

## Error Handling

- `Suspend()`/`Resume()` are idempotent — safe to call multiple times
- `Resume()` restarts sync only if `StartMonitoring` / `StartAutoSync` are safe to call on an already-running monitor/coordinator (they are — both have guards)
- `AppViewModel` only restarts UI timer on resume if `IsAuthenticated` — matches existing `StartSyncStatusTimer()` call-site logic

## Testing

| Scenario | Expected |
|---|---|
| App backgrounded on Android | Auto-sync stops, network monitor stops |
| App foregrounded | Both restart, sync fires within 15 min |
| Window minimized on desktop | Same pause behaviour |
| Window restored | Same resume behaviour |
| Double Suspend() call | Event fires once, no double-stop |
| Double Resume() call | Event fires once, no double-start |
| Suspend while not authenticated | No crash (sync already idle) |

## Files Changed

| File | Change |
|---|---|
| `MyBibleApp/Services/AppLifecycleService.cs` | **New** |
| `MyBibleApp/Services/SharedSyncRuntime.cs` | Wire lifecycle events, change interval 2→15 min |
| `MyBibleApp.Android/MainActivity.cs` | Add `OnPause`/`OnResume` overrides |
| `MyBibleApp/Views/AppShellView.axaml.cs` | Subscribe to window minimize/restore |
| `MyBibleApp/ViewModels/AppViewModel.cs` | Subscribe to lifecycle for UI timer |
