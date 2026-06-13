# Tab Sidebar Toggle — Design Spec

**Date:** 2026-06-13

## Overview

Add a toggle to the app menu flyout to show/hide the right-edge passage tabs sidebar (`TabBar`). User preference persists across restarts and wins over the responsive auto-hide behavior.

## Components

### 1. `AppViewModel` (`MyBibleApp/ViewModels/AppViewModel.cs`)

New property `IsTabBarVisible` following the `IsDebugMode` pattern exactly:

- Storage key constant: `TabBarVisibleKey = "tab_bar_visible"`
- Backing field: `private bool _isTabBarVisible = true` (default: visible)
- Setter: `RaiseAndSetIfChanged` + fire-and-forget `PersistTabBarVisibleAsync` via `ILocalStorageProvider`
- Loaded on startup alongside `IsDebugMode` from `ILocalStorageProvider`

### 2. `MainView.axaml` (`MyBibleApp/Views/MainView.axaml`)

Add a `ToggleSwitch` in the app menu flyout (the hamburger menu), alongside the existing Debug Mode toggle. Label: `"Show Tab Sidebar"`. Binds `IsChecked` two-way to `AppVM.IsTabBarVisible`.

### 3. `AppShellView.axaml.cs` (`MyBibleApp/Views/AppShellView.axaml.cs`)

#### New method: `UpdateTabBarVisibility`

Centralizes all tab bar visibility logic:

```csharp
private void UpdateTabBarVisibility()
{
    if (_tabBar == null) return;
    _tabBar.IsVisible = _appVM.IsTabBarVisible && Bounds.Width >= TabBarMinWidth;
}
```

#### `OnShellSizeChanged` update

Replace the current inline assignment:
```csharp
// Before
_tabBar.IsVisible = e.NewSize.Width >= TabBarMinWidth;

// After
UpdateTabBarVisibility();
```

#### `PropertyChanged` subscription (alongside `TrackAuthState` pattern)

Subscribe to `AppViewModel.PropertyChanged` for `IsTabBarVisible`:

```csharp
if (args.PropertyName == nameof(AppViewModel.IsTabBarVisible))
    Dispatcher.UIThread.Post(UpdateTabBarVisibility);
```

Call `UpdateTabBarVisibility()` at initialization to apply the loaded setting immediately.

## Visibility Logic

| User Pref | Width ≥ 600px | TabBar Visible |
|-----------|--------------|----------------|
| true      | true         | true           |
| true      | false        | false          |
| false     | true         | false          |
| false     | false        | false          |

User preference wins: setting to `false` always hides regardless of window width.

## Data Flow

```
User toggles ToggleSwitch in flyout
  → AppVM.IsTabBarVisible setter fires
  → RaiseAndSetIfChanged notifies PropertyChanged
  → AppShellView subscription receives event
  → UpdateTabBarVisibility() called on UI thread
  → TabBar.IsVisible updated
  → PersistTabBarVisibleAsync saves to ILocalStorageProvider
```

On next app launch:
```
AppViewModel.LoadSettingsAsync (or equivalent)
  → Reads "tab_bar_visible" from ILocalStorageProvider
  → Sets _isTabBarVisible (no persist needed, loading)
  → AppShellView.UpdateTabBarVisibility() called at init
```

## Out of Scope

- Animate the sidebar show/hide
- Separate settings for different screen sizes
