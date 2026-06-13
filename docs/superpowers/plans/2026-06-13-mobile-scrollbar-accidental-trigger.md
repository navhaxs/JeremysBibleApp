# Mobile Scrollbar Accidental Trigger Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent the mobile reader progress scrollbar from jumping scroll position on accidental touches near the right edge.

**Architecture:** Two independent guards added to `MainView.axaml.cs`. Guard 1 makes the track non-hit-testable while hidden, so it can't intercept touches when invisible. Guard 2 requires ≥8px drag movement before activating scroll navigation, so accidental taps don't jump position.

**Tech Stack:** Avalonia UI, C#, .NET — no new dependencies

---

## Files

- Modify: `MyBibleApp/Views/MainView.axaml.cs`

No XAML changes required. Both guards are pure C# logic changes to existing pointer event handlers and the show/hide helper.

---

### Task 1: Guard 1 — Invisible = non-hit-testable

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

Context: The init block around line 330 sets `Opacity = 0` for mobile. `ShowScrollbarBriefly()` around line 1005 restores opacity and schedules a hide after 2000ms. The hide callback at line 1019-1020 sets `Opacity = 0` again.

- [ ] **Step 1: Set `IsHitTestVisible = false` on init**

Find the mobile scrollbar init block (around line 329–334):

```csharp
// ── Scrollbar visibility (desktop: always visible; mobile: tap-to-reveal) ──
if (!PlatformHelper.IsDesktop && _readerProgressTrack != null)
{
    _readerProgressTrack.Opacity = 0;
    _paragraphList.AddHandler(TappedEvent, OnListBoxTapped, handledEventsToo: false);
}
```

Change to:

```csharp
// ── Scrollbar visibility (desktop: always visible; mobile: tap-to-reveal) ──
if (!PlatformHelper.IsDesktop && _readerProgressTrack != null)
{
    _readerProgressTrack.Opacity = 0;
    _readerProgressTrack.IsHitTestVisible = false;
    _paragraphList.AddHandler(TappedEvent, OnListBoxTapped, handledEventsToo: false);
}
```

- [ ] **Step 2: Enable hit-testing when showing scrollbar**

Find `ShowScrollbarBriefly()` (around line 1005–1023):

```csharp
private void ShowScrollbarBriefly()
{
    if (_readerProgressTrack == null) return;
    _readerProgressTrack.Opacity = 1;

    _scrollbarHideCts?.Cancel();
    _scrollbarHideCts = new CancellationTokenSource();
    var cts = _scrollbarHideCts;

    _ = Task.Delay(2000, cts.Token).ContinueWith(t =>
    {
        if (t.IsCanceled) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDraggingProgressBar && _readerProgressTrack != null)
                _readerProgressTrack.Opacity = 0;
        });
    }, TaskScheduler.Default);
}
```

Change to:

```csharp
private void ShowScrollbarBriefly()
{
    if (_readerProgressTrack == null) return;
    _readerProgressTrack.Opacity = 1;
    if (!PlatformHelper.IsDesktop)
        _readerProgressTrack.IsHitTestVisible = true;

    _scrollbarHideCts?.Cancel();
    _scrollbarHideCts = new CancellationTokenSource();
    var cts = _scrollbarHideCts;

    _ = Task.Delay(2000, cts.Token).ContinueWith(t =>
    {
        if (t.IsCanceled) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDraggingProgressBar && _readerProgressTrack != null)
            {
                _readerProgressTrack.Opacity = 0;
                if (!PlatformHelper.IsDesktop)
                    _readerProgressTrack.IsHitTestVisible = false;
            }
        });
    }, TaskScheduler.Default);
}
```

- [ ] **Step 3: Build and verify no compile errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: disable scrollbar hit-testing while hidden on mobile"
```

---

### Task 2: Guard 2 — Drag distance threshold

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

Context: Pointer handler fields are around line 84–85. The three handlers are `OnProgressTrackPointerPressed` (line 956), `OnProgressTrackPointerMoved` (line 970), `OnProgressTrackPointerReleased` (line 987).

- [ ] **Step 1: Add new fields**

Find the existing scrollbar fields (around line 84–85):

```csharp
private bool _isDraggingProgressBar;
private CancellationTokenSource? _scrollbarHideCts;
```

Change to:

```csharp
private bool _isDraggingProgressBar;
private bool _isPressedOnTrack;
private double _dragStartY;
private CancellationTokenSource? _scrollbarHideCts;
```

- [ ] **Step 2: Modify `OnProgressTrackPointerPressed`**

Current implementation (around line 956–968):

```csharp
private void OnProgressTrackPointerPressed(object? sender, PointerPressedEventArgs e)
{
    if (_readerProgressTrack == null) return;
    _isDraggingProgressBar = true;
    e.Pointer.Capture(_readerProgressTrack);
    // Keep the scrollbar visible while the thumb is being dragged.
    if (!PlatformHelper.IsDesktop)
        _scrollbarHideCts?.Cancel();
    BuildChapterMarkers();
    var y = e.GetPosition(_readerProgressTrack).Y;
    ScrollToFraction(y / _readerProgressTrack.Bounds.Height);
    e.Handled = true;
}
```

Change to:

```csharp
private void OnProgressTrackPointerPressed(object? sender, PointerPressedEventArgs e)
{
    if (_readerProgressTrack == null) return;
    _isPressedOnTrack = true;
    _dragStartY = e.GetPosition(_readerProgressTrack).Y;
    e.Pointer.Capture(_readerProgressTrack);
    // Keep the scrollbar visible while interaction is in progress.
    if (!PlatformHelper.IsDesktop)
        _scrollbarHideCts?.Cancel();
    e.Handled = true;
}
```

- [ ] **Step 3: Modify `OnProgressTrackPointerMoved`**

Current implementation (around line 970–985):

```csharp
private void OnProgressTrackPointerMoved(object? sender, PointerEventArgs e)
{
    if (!_isDraggingProgressBar || _readerProgressTrack == null) return;
    var y = e.GetPosition(_readerProgressTrack).Y;
    ScrollToFraction(y / _readerProgressTrack.Bounds.Height);

    // Move thumb immediately for responsive feel while scroll catches up.
    if (_readerProgressThumb != null)
    {
        var trackHeight = _readerProgressTrack.Bounds.Height;
        var thumbHeight = _readerProgressThumb.Height;
        var maxTop      = Math.Max(0, trackHeight - thumbHeight);
        Canvas.SetTop(_readerProgressThumb, Math.Clamp(y - thumbHeight / 2, 0, maxTop));
    }
    e.Handled = true;
}
```

Change to:

```csharp
private void OnProgressTrackPointerMoved(object? sender, PointerEventArgs e)
{
    if (!_isPressedOnTrack || _readerProgressTrack == null) return;
    var y = e.GetPosition(_readerProgressTrack).Y;

    if (!_isDraggingProgressBar)
    {
        if (Math.Abs(y - _dragStartY) < 8.0) return;
        _isDraggingProgressBar = true;
        BuildChapterMarkers();
    }

    ScrollToFraction(y / _readerProgressTrack.Bounds.Height);

    // Move thumb immediately for responsive feel while scroll catches up.
    if (_readerProgressThumb != null)
    {
        var trackHeight = _readerProgressTrack.Bounds.Height;
        var thumbHeight = _readerProgressThumb.Height;
        var maxTop      = Math.Max(0, trackHeight - thumbHeight);
        Canvas.SetTop(_readerProgressThumb, Math.Clamp(y - thumbHeight / 2, 0, maxTop));
    }
    e.Handled = true;
}
```

- [ ] **Step 4: Modify `OnProgressTrackPointerReleased`**

Current implementation (around line 987–998):

```csharp
private void OnProgressTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
{
    if (!_isDraggingProgressBar) return;
    _isDraggingProgressBar = false;
    if (_chapterMarkersCanvas != null)
        _chapterMarkersCanvas.IsVisible = false;
    // Restart the auto-hide countdown after the thumb is released.
    if (!PlatformHelper.IsDesktop)
        ShowScrollbarBriefly();
    e.Pointer.Capture(null);
    e.Handled = true;
}
```

Change to:

```csharp
private void OnProgressTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
{
    if (!_isPressedOnTrack) return;
    _isPressedOnTrack = false;
    e.Pointer.Capture(null);

    if (_isDraggingProgressBar)
    {
        _isDraggingProgressBar = false;
        if (_chapterMarkersCanvas != null)
            _chapterMarkersCanvas.IsVisible = false;
    }

    // Restart the auto-hide countdown after interaction ends.
    if (!PlatformHelper.IsDesktop)
        ShowScrollbarBriefly();
    e.Handled = true;
}
```

- [ ] **Step 5: Build and verify no compile errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: build succeeds, 0 errors.

- [ ] **Step 6: Manual verification on Android**

Deploy to Android device or emulator. Verify these scenarios:

| Scenario | Expected |
|---|---|
| Finger drifts right onto scrollbar edge while scrolling content | Scroll position does NOT jump |
| Tap near right edge of screen | Scrollbar briefly appears, position does NOT jump |
| Tap on content (ListBox) | Scrollbar briefly appears (existing behavior) |
| Swipe up/down on scrollbar track ≥8px | Scroll position jumps and chapter markers appear |
| Drag scrollbar thumb to new position | Works normally, chapter markers visible |
| Release drag | Chapter markers hide, scrollbar starts auto-hide timer |
| Fast scroll | Scrollbar briefly appears (existing behavior) |

- [ ] **Step 7: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "fix: require drag threshold before activating scrollbar navigation on mobile"
```
