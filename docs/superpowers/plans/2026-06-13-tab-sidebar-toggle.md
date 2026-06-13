# Tab Sidebar Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a ToggleSwitch in the app menu flyout to show/hide the right-edge passage tabs sidebar, persisted across restarts.

**Architecture:** `AppViewModel` holds `IsTabBarVisible` (persisted via `ILocalStorageProvider`, same pattern as `IsDebugMode`). `AppShellView` subscribes to `PropertyChanged` and calls `UpdateTabBarVisibility()`, which combines user preference with the responsive width threshold. The flyout ToggleSwitch binds two-way to the VM property.

**Tech Stack:** Avalonia UI (`.axaml`), ReactiveUI (`RaiseAndSetIfChanged`, `PropertyChanged`), file-based `ILocalStorageProvider`

---

## File Map

| File | Change |
|------|--------|
| `MyBibleApp/ViewModels/AppViewModel.cs` | Add `TabBarVisibleKey`, `_isTabBarVisible`, `IsTabBarVisible`, `PersistTabBarVisibleAsync`, `LoadTabBarVisibleFromStorageAsync` |
| `MyBibleApp/Views/AppShellView.axaml.cs` | Add `UpdateTabBarVisibility()`, update `OnShellSizeChanged`, subscribe to `IsTabBarVisible` in `PropertyChanged` handler, call at init |
| `MyBibleApp/Views/MainView.axaml` | Add ToggleSwitch for `IsTabBarVisible` in app menu flyout |

---

### Task 1: Add `IsTabBarVisible` to `AppViewModel`

**Files:**
- Modify: `MyBibleApp/ViewModels/AppViewModel.cs`

- [ ] **Step 1: Add the storage key constant**

In `AppViewModel.cs`, find the constants block (near `DebugModeKey`):
```csharp
    private const string LocalTabStateKey = "LocalTabState";
    private const string DebugModeKey = "IsDebugMode";
    private const string ThemeKey = "SelectedThemeId";
```
Add `TabBarVisibleKey` after `DebugModeKey`:
```csharp
    private const string LocalTabStateKey = "LocalTabState";
    private const string DebugModeKey = "IsDebugMode";
    private const string TabBarVisibleKey = "IsTabBarVisible";
    private const string ThemeKey = "SelectedThemeId";
```

- [ ] **Step 2: Add the backing field**

Find the `_isDebugMode` backing field and add `_isTabBarVisible` alongside it:
```csharp
    private bool _isDebugMode;
    private bool _isTabBarVisible = true;
```

- [ ] **Step 3: Add the property and persist methods**

In `AppViewModel.cs`, immediately after the `PersistDebugModeAsync` method and `LoadDebugModeFromStorageAsync` method, add:

```csharp
    // ── Tab Bar Visible ──────────────────────────────────────────────────────

    public bool IsTabBarVisible
    {
        get => _isTabBarVisible;
        set
        {
            var old = _isTabBarVisible;
            this.RaiseAndSetIfChanged(ref _isTabBarVisible, value);
            if (old != _isTabBarVisible)
                _ = PersistTabBarVisibleAsync(_isTabBarVisible);
        }
    }

    private async Task PersistTabBarVisibleAsync(bool value)
    {
        if (_localStorageProvider == null) return;
        try
        {
            await _localStorageProvider.SaveAsync(TabBarVisibleKey, value ? "true" : "false").ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    public async Task LoadTabBarVisibleFromStorageAsync()
    {
        if (_localStorageProvider == null) return;
        try
        {
            var stored = await _localStorageProvider.GetAsync(TabBarVisibleKey).ConfigureAwait(false);
            if (string.Equals(stored, "false", StringComparison.OrdinalIgnoreCase))
                await Dispatcher.UIThread.InvokeAsync(() => _isTabBarVisible = false);
            this.RaisePropertyChanged(nameof(IsTabBarVisible));
        }
        catch { /* best-effort */ }
    }
```

Note: The load logic defaults to `true` (visible) when no stored value exists — only sets `false` when the stored value is explicitly `"false"`. This mirrors the `LoadDebugModeFromStorageAsync` pattern but inverted for a visible-by-default setting.

- [ ] **Step 4: Build to verify no compile errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/ViewModels/AppViewModel.cs
git commit -m "feat: add IsTabBarVisible property to AppViewModel with persistence"
```

---

### Task 2: Load setting on startup and wire `AppShellView`

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs`

- [ ] **Step 1: Call `LoadTabBarVisibleFromStorageAsync` on startup**

In `AppShellView.axaml.cs`, find `RestoreTabsAndAuthAsync()` (around line 587). It contains:
```csharp
    await _appVM.LoadDebugModeFromStorageAsync();
```
Add the tab bar load call immediately after:
```csharp
    await _appVM.LoadDebugModeFromStorageAsync();
    await _appVM.LoadTabBarVisibleFromStorageAsync();
```

- [ ] **Step 2: Add `UpdateTabBarVisibility` method**

In `AppShellView.axaml.cs`, find `OnShellSizeChanged`:
```csharp
    private void OnShellSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_tabBar != null)
            _tabBar.IsVisible = e.NewSize.Width >= TabBarMinWidth;
    }
```
Replace it with:
```csharp
    private void UpdateTabBarVisibility()
    {
        if (_tabBar == null) return;
        _tabBar.IsVisible = _appVM.IsTabBarVisible && Bounds.Width >= TabBarMinWidth;
    }

    private void OnShellSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateTabBarVisibility();
    }
```

- [ ] **Step 3: Subscribe to `IsTabBarVisible` in `PropertyChanged` handler**

Find `TrackAuthState()`:
```csharp
    private void TrackAuthState()
    {
        _authStateHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(AppViewModel.IsAuthenticating))
                Dispatcher.UIThread.Post(UpdateSignInOverlayVisibility);
        };

        _appVM.PropertyChanged += _authStateHandler;
        UpdateSignInOverlayVisibility();
    }
```
Add the `IsTabBarVisible` check inside the existing handler lambda (do not add a second subscription):
```csharp
    private void TrackAuthState()
    {
        _authStateHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(AppViewModel.IsAuthenticating))
                Dispatcher.UIThread.Post(UpdateSignInOverlayVisibility);
            if (args.PropertyName == nameof(AppViewModel.IsTabBarVisible))
                Dispatcher.UIThread.Post(UpdateTabBarVisibility);
        };

        _appVM.PropertyChanged += _authStateHandler;
        UpdateSignInOverlayVisibility();
    }
```

- [ ] **Step 4: Call `UpdateTabBarVisibility` at init**

Find where `TrackAuthState()` is called during initialization. Add `UpdateTabBarVisibility()` call immediately after it:
```csharp
    TrackAuthState();
    UpdateTabBarVisibility();
```

- [ ] **Step 5: Build to verify no compile errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Views/AppShellView.axaml.cs
git commit -m "feat: wire tab bar visibility to AppViewModel.IsTabBarVisible"
```

---

### Task 3: Add ToggleSwitch to app menu flyout

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml`

- [ ] **Step 1: Add the ToggleSwitch**

In `MainView.axaml`, find the existing Debug Mode ToggleSwitch (around line 265):
```axaml
                                    <ToggleSwitch
                                        IsChecked="{Binding AppVM.IsDebugMode, Mode=TwoWay}"
                                        OffContent="Debug mode"
                                        OnContent="Debug mode" />
```
Add the Tab Sidebar toggle immediately after it:
```axaml
                                    <ToggleSwitch
                                        IsChecked="{Binding AppVM.IsDebugMode, Mode=TwoWay}"
                                        OffContent="Debug mode"
                                        OnContent="Debug mode" />
                                    <ToggleSwitch
                                        IsChecked="{Binding AppVM.IsTabBarVisible, Mode=TwoWay}"
                                        OffContent="Show tab sidebar"
                                        OnContent="Show tab sidebar" />
```

- [ ] **Step 2: Build to verify no compile errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the app and verify manually**

Launch the app (Desktop target). Open the hamburger menu. Verify:
- "Show tab sidebar" ToggleSwitch appears below "Debug mode"
- Toggling off hides the right-edge passage tabs immediately
- Toggling on shows them (if window width ≥ 600px)
- Narrowing the window below 600px hides tabs even when toggle is on
- Closing and reopening the app preserves the last toggle state

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml
git commit -m "feat: add tab sidebar toggle to app menu flyout"
```
