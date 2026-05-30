# Dotted Page Pattern Background Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a toggleable dotted page pattern to the Bible reading area, with background color from the active theme and a menu toggle that persists across sessions.

**Architecture:** A new `IsDotPatternEnabled` bool in `AppViewModel` mirrors the existing `IsDebugMode` pattern (persist/load via `ILocalStorageProvider`). `ApplyTheme()` builds a tiled `DrawingBrush` combining background fill + dot geometry and stores it as `ThemeDotPatternBrush` in app resources. A `Border` at `ZIndex="-2"` in `InkAreaGrid` shows the pattern when the toggle is on.

**Tech Stack:** Avalonia UI, ReactiveUI, `ILocalStorageProvider`, `Avalonia.Media.DrawingBrush`

---

## File Map

| File | Change |
|---|---|
| `MyBibleApp/ViewModels/AppViewModel.cs` | Add `IsDotPatternEnabled` property + persist/load methods |
| `MyBibleApp/Views/MainView.axaml.cs` | Add `BuildDotPatternBrush` helper, call it in `ApplyTheme` |
| `MyBibleApp/Views/MainView.axaml` | Add dot pattern `Border`, set `ListBox` `Background="Transparent"`, add menu `ToggleSwitch` |
| `MyBibleApp/Views/AppShellView.axaml.cs` | Call `LoadDotPatternFromStorageAsync()` in `RestoreTabsAndAuthAsync` |

---

### Task 1: Add `IsDotPatternEnabled` to `AppViewModel`

**Files:**
- Modify: `MyBibleApp/ViewModels/AppViewModel.cs`

No unit test needed — this is an identical pattern to `IsDebugMode`, which delegates entirely to `ILocalStorageProvider` (already tested in `MyBibleApp.Sync.Tests`).

- [ ] **Step 1: Add the constant and backing field**

In `MyBibleApp/ViewModels/AppViewModel.cs`, add to the constants block (after `ThemeKey` on line 18) and the fields block (after `_isDebugMode` on line 27):

```csharp
private const string DotPatternKey = "IsDotPatternEnabled";
```

```csharp
private bool _isDotPatternEnabled;
```

- [ ] **Step 2: Add the property**

After the `IsDebugMode` property block (after line 88 — the closing brace of `PersistDebugModeAsync`), add:

```csharp
// ── Dot Pattern ──────────────────────────────────────────────────────────

public bool IsDotPatternEnabled
{
    get => _isDotPatternEnabled;
    set
    {
        var old = _isDotPatternEnabled;
        this.RaiseAndSetIfChanged(ref _isDotPatternEnabled, value);
        if (old != _isDotPatternEnabled)
            _ = PersistDotPatternAsync(_isDotPatternEnabled);
    }
}

private async Task PersistDotPatternAsync(bool value)
{
    if (_localStorageProvider == null) return;
    try
    {
        await _localStorageProvider.SaveAsync(DotPatternKey, value ? "true" : "false").ConfigureAwait(false);
    }
    catch { /* best-effort */ }
}

public async Task LoadDotPatternFromStorageAsync()
{
    if (_localStorageProvider == null) return;
    try
    {
        var stored = await _localStorageProvider.GetAsync(DotPatternKey).ConfigureAwait(false);
        if (string.Equals(stored, "true", StringComparison.OrdinalIgnoreCase))
            await Dispatcher.UIThread.InvokeAsync(() => _isDotPatternEnabled = true);
        this.RaisePropertyChanged(nameof(IsDotPatternEnabled));
    }
    catch { /* best-effort */ }
}
```

- [ ] **Step 3: Build and verify no errors**

```powershell
cd C:\Users\Jeremy\RiderProjects\OpenBibleApp
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/ViewModels/AppViewModel.cs
git commit -m "feat: add IsDotPatternEnabled setting to AppViewModel"
```

---

### Task 2: Build `ThemeDotPatternBrush` in `ApplyTheme`

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

No new using directives needed — `Avalonia`, `Avalonia.Media`, and `Avalonia.Styling` are already imported.

- [ ] **Step 1: Add the `BuildDotPatternBrush` helper**

Add this private static method to `MainView.axaml.cs`, anywhere in the class body (e.g. directly after the `ApplyTheme` method at line ~1812):

```csharp
private static DrawingBrush BuildDotPatternBrush(Models.AppTheme theme)
{
    var bgColor = theme.BackgroundOverride ?? theme.SwatchColor;
    var dotColor = theme.Variant == ThemeVariant.Dark
        ? Color.FromArgb(45, 255, 255, 255)
        : Color.FromArgb(45, 0, 0, 0);

    const double tileSize = 20;
    const double dotRadius = 1.5;

    return new DrawingBrush
    {
        TileMode = TileMode.Tile,
        DestinationRect = new RelativeRect(0, 0, tileSize, tileSize, RelativeUnit.Absolute),
        Drawing = new DrawingGroup
        {
            Children =
            [
                new GeometryDrawing
                {
                    Brush = new SolidColorBrush(bgColor),
                    Geometry = new RectangleGeometry { Rect = new Rect(0, 0, tileSize, tileSize) }
                },
                new GeometryDrawing
                {
                    Brush = new SolidColorBrush(dotColor),
                    Geometry = new EllipseGeometry
                    {
                        Center = new Point(tileSize / 2, tileSize / 2),
                        RadiusX = dotRadius,
                        RadiusY = dotRadius
                    }
                }
            ]
        }
    };
}
```

- [ ] **Step 2: Call it in `ApplyTheme`**

In `ApplyTheme` (line ~1797), add one line at the end of the method body, after the foreground override block:

```csharp
public void ApplyTheme(Models.AppTheme theme)
{
    if (Application.Current == null) return;
    Application.Current.RequestedThemeVariant = theme.Variant;

    if (theme.BackgroundOverride is { } bg)
        Application.Current.Resources["ThemeBackgroundBrush"] = new SolidColorBrush(bg);
    else
        Application.Current.Resources.Remove("ThemeBackgroundBrush");

    if (theme.ForegroundOverride is { } fg)
        Application.Current.Resources["ThemeForegroundBrush"] = new SolidColorBrush(fg);
    else
        Application.Current.Resources.Remove("ThemeForegroundBrush");

    Application.Current.Resources["ThemeDotPatternBrush"] = BuildDotPatternBrush(theme);
}
```

- [ ] **Step 3: Build and verify no errors**

```powershell
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: build ThemeDotPatternBrush in ApplyTheme"
```

---

### Task 3: Wire up XAML — dot pattern border + menu toggle

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml`

- [ ] **Step 1: Add the dot pattern `Border` in `InkAreaGrid`**

In `MainView.axaml`, find `InkAreaGrid` (line ~373). Add a `Border` as the first child, before `PenUnderlay`:

```xml
<Grid x:Name="InkAreaGrid" Grid.Row="2" RowDefinitions="*" Margin="0,0,0,0" Background="Transparent">
  <!-- Dot page pattern — bottom-most layer, behind pen strokes and text -->
  <Border Grid.Row="0"
          ZIndex="-2"
          IsVisible="{Binding AppVM.IsDotPatternEnabled}"
          Background="{DynamicResource ThemeDotPatternBrush}" />
  <!-- Pen ink underlay — below the scripture text, SrcOver blend -->
  <controls:InkOverlayCanvas Grid.Row="0" x:Name="PenUnderlay" IsHitTestVisible="False" ZIndex="-1" />
  <!-- ListBox for Bible content -->
  <ListBox Grid.Row="0"
           Background="Transparent"
           Name="ParagraphList"
           SelectionMode="Single"
           SelectionChanged="OnParagraphListSelectionChanged"
           ScrollViewer.HorizontalScrollBarVisibility="Disabled"
           ScrollViewer.VerticalScrollBarVisibility="Hidden">
```

The only changes are: insert the `Border` element, and add `Background="Transparent"` to `ParagraphList`.

- [ ] **Step 2: Add the menu `ToggleSwitch`**

In the menu flyout `StackPanel` (line ~210), add after the debug mode `ToggleSwitch` (line ~219):

```xml
<ToggleSwitch OffContent="Debug mode"
              OnContent="Debug mode"
              IsChecked="{Binding AppVM.IsDebugMode, Mode=TwoWay}" />
<ToggleSwitch OffContent="Page pattern"
              OnContent="Page pattern"
              IsChecked="{Binding AppVM.IsDotPatternEnabled, Mode=TwoWay}" />
```

- [ ] **Step 3: Build and verify no errors**

```powershell
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml
git commit -m "feat: add dot pattern border and menu toggle to MainView"
```

---

### Task 4: Load dot pattern setting on startup

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs`

- [ ] **Step 1: Add `LoadDotPatternFromStorageAsync` call**

In `RestoreTabsAndAuthAsync` (line ~561), add the load call immediately after `LoadDebugModeFromStorageAsync`:

```csharp
private async Task RestoreTabsAndAuthAsync()
{
    // Load persisted debug mode state early so the overlay is visible during restore.
    await _appVM.LoadDebugModeFromStorageAsync();
    await _appVM.LoadDotPatternFromStorageAsync();

    // Load persisted theme and apply it.
    await _appVM.LoadThemeFromStorageAsync();
    var theme = Models.AppTheme.GetById(_appVM.SelectedThemeId);
    _primaryView?.ApplyTheme(theme);
    ...
```

- [ ] **Step 2: Build and verify no errors**

```powershell
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the app and verify end-to-end**

Launch the app. Open the menu (hamburger). Confirm "Page pattern" toggle appears below "Debug mode". Toggle it on — dots should appear in the reading area behind text. Switch themes — dots should update to the new theme's background. Close and reopen app — toggle state should persist.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/AppShellView.axaml.cs
git commit -m "feat: load dot pattern setting on startup"
```
