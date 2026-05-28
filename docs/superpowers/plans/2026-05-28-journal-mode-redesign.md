# Journal Mode Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the standalone `JournalModeView`/`JournalListView` screens with per-tab journal mode integrated into the existing app shell, where all ink routes to a named journal or an ephemeral in-memory buffer.

**Architecture:** `InkOverlayCanvas` gains a `StrokeCompleted` event; `AppShellView` routes each completed stroke to either `JournalStore` (named journal) or a per-tab ephemeral list. A new `JournalFlyoutView` replaces the old full-screen journal screens. Tab state gains `ActiveJournalId` and `EphemeralInkStrokes`.

**Tech Stack:** Avalonia (`net10.0`), C# 13, xUnit + FsCheck (`MyBibleApp.Journal.Tests`), SkiaSharp (InkOverlayCanvas)

---

## File Map

| Action | Path | Responsibility |
|--------|------|----------------|
| Modify | `MyBibleApp/Models/Journal.cs` | Add passage anchor fields to `JournalInkStroke` |
| Modify | `MyBibleApp/Services/IJournalStore.cs` | Add `AppendInkStrokeAsync`, `RemoveInkStrokeAsync` |
| Modify | `MyBibleApp/Services/JournalStore.cs` | Implement new methods |
| Modify | `MyBibleApp/Controls/InkOverlayCanvas.cs` | Add `StrokeCompleted`, `StrokeUndone` events; `LoadJournalStrokes` method |
| Modify | `MyBibleApp/Views/MainView.axaml` | Add journal flyout button to toolbar |
| Modify | `MyBibleApp/Views/MainView.axaml.cs` | Wire journal button; surface `StrokeCompleted`, `StrokeUndone` events; add `SetJournalLayout` |
| Modify | `MyBibleApp/Views/AppShellView.axaml` | Add `JournalFlyoutView`; remove `JournalListView`, `JournalModeView` |
| Modify | `MyBibleApp/Views/AppShellView.axaml.cs` | Tab journal state; stroke routing; flyout wiring |
| Create | `MyBibleApp/ViewModels/JournalFlyoutViewModel.cs` | Journal list, create/delete/activate/save-as |
| Create | `MyBibleApp/Views/JournalFlyoutView.axaml` | Flyout panel AXAML |
| Create | `MyBibleApp/Views/JournalFlyoutView.axaml.cs` | Flyout code-behind |
| Delete | `MyBibleApp/Views/JournalModeView.axaml[.cs]` | Obsolete |
| Delete | `MyBibleApp/Views/JournalListView.axaml[.cs]` | Obsolete |
| Delete | `MyBibleApp/ViewModels/JournalModeViewModel.cs` | Obsolete |
| Delete | `MyBibleApp/ViewModels/JournalListViewModel.cs` | Obsolete |
| Delete | `MyBibleApp/Controls/JournalInkCanvas.cs` | Obsolete |

---

## Task 1: Add passage anchor fields to JournalInkStroke

**Files:**
- Modify: `MyBibleApp/Models/Journal.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/JournalInkStrokeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `MyBibleApp.Journal.Tests/Unit/JournalInkStrokeTests.cs`:

```csharp
using MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class JournalInkStrokeTests
{
    [Fact]
    public void JournalInkStroke_HasPassageAnchorFields()
    {
        var stroke = new JournalInkStroke
        {
            Id = "s1",
            Points = [new StrokePoint(10, 20), new StrokePoint(30, 40)],
            Color = "#FF000000",
            StrokeWidth = 2.5,
            IsHighlight = false,
            BookCode = "GEN",
            ChapterNumber = 1,
            AnchorParagraphIndex = 3,
            AnchorContentTop = 150.0
        };

        Assert.Equal("GEN", stroke.BookCode);
        Assert.Equal(1, stroke.ChapterNumber);
        Assert.Equal(3, stroke.AnchorParagraphIndex);
        Assert.Equal(150.0, stroke.AnchorContentTop);
    }

    [Fact]
    public void JournalInkStroke_DefaultAnchorParagraphIndex_IsMinusOne()
    {
        var stroke = new JournalInkStroke { Id = "s2" };
        Assert.Equal(-1, stroke.AnchorParagraphIndex);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test MyBibleApp.Journal.Tests --filter "JournalInkStrokeTests"
```
Expected: compile error — `BookCode`, `ChapterNumber`, `AnchorParagraphIndex`, `AnchorContentTop` not found.

- [ ] **Step 3: Add fields to JournalInkStroke in `MyBibleApp/Models/Journal.cs`**

Find `JournalInkStroke` class and add four properties after `IsHighlight`:

```csharp
public sealed class JournalInkStroke
{
    public string Id { get; init; } = string.Empty;
    public IReadOnlyList<StrokePoint> Points { get; init; } = [];
    public string Color { get; init; } = string.Empty;
    public double StrokeWidth { get; init; }
    public bool IsHighlight { get; init; }
    public string BookCode { get; init; } = string.Empty;
    public int ChapterNumber { get; init; }
    public int AnchorParagraphIndex { get; init; } = -1;
    public double AnchorContentTop { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

```
dotnet test MyBibleApp.Journal.Tests --filter "JournalInkStrokeTests"
```
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```
git add MyBibleApp/Models/Journal.cs MyBibleApp.Journal.Tests/Unit/JournalInkStrokeTests.cs
git commit -m "feat: add passage anchor fields to JournalInkStroke"
```

---

## Task 2: Add AppendInkStrokeAsync and RemoveInkStrokeAsync to IJournalStore + JournalStore

**Files:**
- Modify: `MyBibleApp/Services/IJournalStore.cs`
- Modify: `MyBibleApp/Services/JournalStore.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/JournalStoreAppendTests.cs`

- [ ] **Step 1: Write failing tests**

Create `MyBibleApp.Journal.Tests/Unit/JournalStoreAppendTests.cs`:

```csharp
using MyBibleApp.Models;
using MyBibleApp.Services;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class JournalStoreAppendTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"journal_test_{Guid.NewGuid()}.json");
    private readonly JournalStore _store;

    public JournalStoreAppendTests() => _store = new JournalStore(_tempPath);

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private async Task<Journal> CreateTestJournalAsync()
    {
        var result = await _store.CreateJournalAsync(new JournalCreateRequest
        {
            Name = "Test",
            TranslationId = "",
            TranslationVersionDate = "",
            ContentHash = "",
            BookCode = "GEN",
            StartChapter = 1,
            StartVerse = 1,
            EndChapter = 1,
            EndVerse = 31,
            Layout = new JournalLayout
            {
                TextColumnWidthDip = 600,
                LeftMarginDip = 80,
                RightMarginDip = 115,
                FontFamily = "Inter",
                FontSizeDip = 16,
                LineHeightDip = 24
            }
        });
        return result.Value!;
    }

    [Fact]
    public async Task AppendInkStrokeAsync_AddsStrokeToJournal()
    {
        var journal = await CreateTestJournalAsync();
        var stroke = new JournalInkStroke
        {
            Id = Guid.NewGuid().ToString(),
            Points = [new StrokePoint(10, 20), new StrokePoint(30, 40)],
            Color = "#FF000000",
            StrokeWidth = 2.5,
            IsHighlight = false,
            BookCode = "GEN",
            ChapterNumber = 1,
            AnchorParagraphIndex = 0,
            AnchorContentTop = 100.0
        };

        var appendResult = await _store.AppendInkStrokeAsync(journal.Id, stroke);

        Assert.True(appendResult.IsSuccess);
        var strokes = await _store.GetInkStrokesAsync(journal.Id);
        Assert.Single(strokes);
        Assert.Equal(stroke.Id, strokes[0].Id);
        Assert.Equal("GEN", strokes[0].BookCode);
        Assert.Equal(1, strokes[0].ChapterNumber);
    }

    [Fact]
    public async Task AppendInkStrokeAsync_AccumulatesMultipleStrokes()
    {
        var journal = await CreateTestJournalAsync();
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();

        await _store.AppendInkStrokeAsync(journal.Id, new JournalInkStroke { Id = id1, BookCode = "GEN", ChapterNumber = 1 });
        await _store.AppendInkStrokeAsync(journal.Id, new JournalInkStroke { Id = id2, BookCode = "ROM", ChapterNumber = 8 });

        var strokes = await _store.GetInkStrokesAsync(journal.Id);
        Assert.Equal(2, strokes.Count);
    }

    [Fact]
    public async Task RemoveInkStrokeAsync_RemovesStrokeById()
    {
        var journal = await CreateTestJournalAsync();
        var id = Guid.NewGuid().ToString();
        await _store.AppendInkStrokeAsync(journal.Id, new JournalInkStroke { Id = id, BookCode = "GEN", ChapterNumber = 1 });

        var removeResult = await _store.RemoveInkStrokeAsync(journal.Id, id);

        Assert.True(removeResult.IsSuccess);
        var strokes = await _store.GetInkStrokesAsync(journal.Id);
        Assert.Empty(strokes);
    }

    [Fact]
    public async Task AppendInkStrokeAsync_ReturnsFailure_WhenJournalNotFound()
    {
        var result = await _store.AppendInkStrokeAsync("nonexistent-id", new JournalInkStroke { Id = "s1" });
        Assert.False(result.IsSuccess);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test MyBibleApp.Journal.Tests --filter "JournalStoreAppendTests"
```
Expected: compile error — `AppendInkStrokeAsync`, `RemoveInkStrokeAsync` not on `IJournalStore`.

- [ ] **Step 3: Add method signatures to `MyBibleApp/Services/IJournalStore.cs`**

Add after the `SaveInkStrokesAsync` line:

```csharp
Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke);
Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId);
```

- [ ] **Step 4: Implement in `MyBibleApp/Services/JournalStore.cs`**

Add these two methods (inside the class, after the existing `SaveInkStrokesAsync` method):

```csharp
public async Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke)
{
    await _semaphore.WaitAsync();
    try
    {
        var (entries, tombstones) = await LoadEntriesAsync();
        var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
        if (entry == null)
            return Result.Failure($"Journal '{journalId}' not found.");

        entry.InkStrokes.Add(stroke);
        await SaveEntriesAsync(entries, tombstones);
        return Result.Success();
    }
    catch (Exception ex)
    {
        return Result.Failure(ex.Message);
    }
    finally
    {
        _semaphore.Release();
    }
}

public async Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId)
{
    await _semaphore.WaitAsync();
    try
    {
        var (entries, tombstones) = await LoadEntriesAsync();
        var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
        if (entry == null)
            return Result.Failure($"Journal '{journalId}' not found.");

        var removed = entry.InkStrokes.RemoveAll(s => s.Id == strokeId);
        if (removed > 0)
            await SaveEntriesAsync(entries, tombstones);
        return Result.Success();
    }
    catch (Exception ex)
    {
        return Result.Failure(ex.Message);
    }
    finally
    {
        _semaphore.Release();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```
dotnet test MyBibleApp.Journal.Tests --filter "JournalStoreAppendTests"
```
Expected: PASS (4 tests)

- [ ] **Step 6: Commit**

```
git add MyBibleApp/Services/IJournalStore.cs MyBibleApp/Services/JournalStore.cs MyBibleApp.Journal.Tests/Unit/JournalStoreAppendTests.cs
git commit -m "feat: add AppendInkStrokeAsync and RemoveInkStrokeAsync to JournalStore"
```

---

## Task 3: Add StrokeCompleted event + LoadJournalStrokes to InkOverlayCanvas

**Files:**
- Modify: `MyBibleApp/Controls/InkOverlayCanvas.cs`

No automated test (Avalonia controls require rendering context). Verify manually by running app and drawing a stroke.

- [ ] **Step 1: Add `InkStrokeEventArgs` class and events to `InkOverlayCanvas.cs`**

Add the public event args class and the two events near the top of `InkOverlayCanvas.cs`, after the existing `InkMode` enum or similar declarations:

```csharp
public sealed class InkStrokeEventArgs : EventArgs
{
    public required IReadOnlyList<Point> Points { get; init; }
    public required Color Color { get; init; }
    public required double StrokeWidth { get; init; }
    public required bool IsHighlight { get; init; }
    public required int AnchorParagraphIndex { get; init; }
    public required double AnchorContentTop { get; init; }
}
```

Add these two public events in the `InkOverlayCanvas` class body (after the existing `StyledProperty` declarations):

```csharp
public event EventHandler<InkStrokeEventArgs>? StrokeCompleted;
public event EventHandler? StrokeUndone;
```

- [ ] **Step 2: Fire `StrokeCompleted` at the end of `EndStroke()`**

In `EndStroke()`, after the `_cachedStrokes.Add(...)` call (both branches — single-dot and multi-point), add this block before `InvalidateVisual()`:

For the single-dot branch (after `_cachedStrokes.Add(new StrokeCache(null, p, ...))`):
```csharp
StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
{
    Points = [],
    Color = _activeStrokeColor,
    StrokeWidth = _activeStrokeWidth,
    IsHighlight = _activeIsHighlight,
    AnchorParagraphIndex = _activeAnchorIndex,
    AnchorContentTop = _activeAnchorContentTop
});
```

For the multi-point branch (after `_cachedStrokes.Add(new StrokeCache(BuildGeometry(...), ...))`), where `pts` is the `IReadOnlyList<Point>` of finalized points:
```csharp
StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
{
    Points = pts,
    Color = _activeStrokeColor,
    StrokeWidth = _activeStrokeWidth,
    IsHighlight = _activeIsHighlight,
    AnchorParagraphIndex = _activeAnchorIndex,
    AnchorContentTop = _activeAnchorContentTop
});
```

- [ ] **Step 3: Fire `StrokeUndone` at the end of `UndoStroke()`**

`UndoStroke()` is at approximately line 230. Add one line at the end:

```csharp
public void UndoStroke()
{
    if (_cachedStrokes.Count == 0) return;
    _cachedStrokes.RemoveAt(_cachedStrokes.Count - 1);
    InvalidateVisual();
    StrokeUndone?.Invoke(this, EventArgs.Empty);  // ADD THIS LINE
}
```

- [ ] **Step 4: Add `LoadJournalStrokes` method**

Add this public method to `InkOverlayCanvas`. It rebuilds `StrokeCache` entries using the same `BuildGeometry` and `ComputeBounds` private methods already used by `EndStroke`:

```csharp
public void LoadJournalStrokes(IReadOnlyList<JournalInkStroke> strokes)
{
    _cachedStrokes.Clear();
    foreach (var stroke in strokes)
    {
        var pts = stroke.Points.Select(p => new Point(p.X, p.Y)).ToList();
        var color = Color.Parse(stroke.Color.Length > 0 ? stroke.Color : "#FF000000");

        if (pts.Count == 0)
            continue;

        if (pts.Count == 1)
        {
            var p = pts[0];
            _cachedStrokes.Add(new StrokeCache(
                null, p,
                new Rect(p.X - 2, p.Y - 2, 4, 4),
                color, stroke.StrokeWidth, stroke.IsHighlight, null,
                stroke.AnchorParagraphIndex, stroke.AnchorContentTop));
        }
        else
        {
            _cachedStrokes.Add(new StrokeCache(
                BuildGeometry(pts),
                default,
                ComputeBounds(pts),
                color, stroke.StrokeWidth, stroke.IsHighlight,
                pts,
                stroke.AnchorParagraphIndex, stroke.AnchorContentTop));
        }
    }
    InvalidateVisual();
}
```

Note: `LoadJournalStrokes` is in the same class as `BuildGeometry` and `ComputeBounds`, so no visibility change needed.

- [ ] **Step 5: Verify compilation**

```
dotnet build MyBibleApp
```
Expected: 0 errors

- [ ] **Step 6: Commit**

```
git add MyBibleApp/Controls/InkOverlayCanvas.cs
git commit -m "feat: add StrokeCompleted/StrokeUndone events and LoadJournalStrokes to InkOverlayCanvas"
```

---

## Task 4: Update MainView — journal flyout button + forwarded events + SetJournalLayout

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml`
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

- [ ] **Step 1: Add journal button to `MainView.axaml`**

In `MainView.axaml`, find the `AnnotationSection` `Border` (x:Name="AnnotationSection"). Inside its `StackPanel`, add a journal button after `UndoButton`:

```xml
<Button x:Name="JournalButton"
        Classes="tool-btn"
        ToolTip.Tip="Journal">
    <Panel>
        <material:MaterialIcon Kind="BookOpenPageVariant" Width="20" Height="20" />
        <Border x:Name="JournalUnsavedBadge"
                IsVisible="False"
                Width="8" Height="8"
                CornerRadius="4"
                Background="{DynamicResource SystemAccentColor}"
                HorizontalAlignment="Right"
                VerticalAlignment="Top"
                Margin="0,0,-2,-2" />
    </Panel>
</Button>
```

Also add a `TextBlock` in the same toolbar area (outside `AnnotationSection`, visible always) to show the active journal name. Add this inside the toolbar `Grid`, in a new row or after the annotation toggle — find `AnnotationToggle` at `Grid.Column="3"` and add at `Grid.Column="4"` (adjusting `ColumnDefinitions` from `"Auto,*,Auto,Auto,Auto,Auto"` to `"Auto,*,Auto,Auto,Auto,Auto,Auto"`):

```xml
<TextBlock Grid.Column="4"
           x:Name="ActiveJournalLabel"
           VerticalAlignment="Center"
           FontSize="12"
           Opacity="0.7"
           IsVisible="False"
           TextTrimming="CharacterEllipsis"
           MaxWidth="120" />
```

- [ ] **Step 2: Wire journal button in `MainView.axaml.cs`**

Add private fields near the other toolbar fields:

```csharp
private Button? _journalButton;
private Border? _journalUnsavedBadge;
private TextBlock? _activeJournalLabel;
```

Add events near `BibleReadingRequested`:

```csharp
public event EventHandler? JournalFlyoutRequested;
public new event EventHandler<InkStrokeEventArgs>? StrokeCompleted;
public event EventHandler? StrokeUndone;
```

In `OnLoaded`, find the controls:

```csharp
_journalButton = this.FindControl<Button>("JournalButton");
_journalUnsavedBadge = this.FindControl<Border>("JournalUnsavedBadge");
_activeJournalLabel = this.FindControl<TextBlock>("ActiveJournalLabel");

if (_journalButton != null)
    _journalButton.Click += (_, _) => JournalFlyoutRequested?.Invoke(this, EventArgs.Empty);
```

Wire up the ink canvas events (add after `_inkOverlay.GetParagraphContentTop = ...`):

```csharp
_inkOverlay.StrokeCompleted += (s, e) => StrokeCompleted?.Invoke(this, e);
_inkOverlay.StrokeUndone += (s, e) => StrokeUndone?.Invoke(this, EventArgs.Empty);
```

Add public methods for `AppShellView` to call:

```csharp
public void SetActiveJournalName(string? name)
{
    if (_activeJournalLabel == null) return;
    _activeJournalLabel.Text = name;
    _activeJournalLabel.IsVisible = name != null;
}

public void SetUnsavedBadgeVisible(bool visible)
{
    if (_journalUnsavedBadge != null)
        _journalUnsavedBadge.IsVisible = visible;
}

public void SetJournalLayout(JournalLayout? layout)
{
    // Applied in Task 9. Placeholder: no-op for now.
}
```

- [ ] **Step 3: Verify compilation**

```
dotnet build MyBibleApp
```
Expected: 0 errors

- [ ] **Step 4: Commit**

```
git add MyBibleApp/Views/MainView.axaml MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: add journal flyout button and stroke forwarding events to MainView"
```

---

## Task 5: Create JournalFlyoutViewModel

**Files:**
- Create: `MyBibleApp/ViewModels/JournalFlyoutViewModel.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/JournalFlyoutViewModelTests.cs`

- [ ] **Step 1: Write failing tests**

Create `MyBibleApp.Journal.Tests/Unit/JournalFlyoutViewModelTests.cs`:

```csharp
using MyBibleApp.Models;
using MyBibleApp.Services;
using MyBibleApp.ViewModels;
using NSubstitute;  // or Moq — use whatever mock framework the test project uses
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class JournalFlyoutViewModelTests
{
    // NOTE: If NSubstitute is not available, use a manual fake IJournalStore.
    // If the project uses no mocking framework, create FakeJournalStore below.
}

// Manual fake for testing without a mock framework:
file sealed class FakeJournalStore : IJournalStore
{
    private readonly List<JournalEntry> _entries = [];
    
    public Task<Result<Journal>> CreateJournalAsync(JournalCreateRequest request)
    {
        var journal = new Journal
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            TranslationId = request.TranslationId,
            TranslationVersionDate = request.TranslationVersionDate,
            BookCode = request.BookCode,
            StartChapter = request.StartChapter,
            StartVerse = request.StartVerse,
            EndChapter = request.EndChapter,
            EndVerse = request.EndVerse,
            ContentHash = request.ContentHash,
            Layout = request.Layout,
            CreatedAtUtc = DateTime.UtcNow,
            LastModifiedUtc = DateTime.UtcNow
        };
        _entries.Add(new JournalEntry { Metadata = journal, InkStrokes = [] });
        return Task.FromResult(Result<Journal>.Success(journal));
    }
    
    public Task<IReadOnlyList<Journal>> GetAllJournalsAsync() =>
        Task.FromResult<IReadOnlyList<Journal>>(_entries.Select(e => e.Metadata).ToList());
    
    public Task<Journal?> GetJournalAsync(string journalId) =>
        Task.FromResult(_entries.FirstOrDefault(e => e.Metadata.Id == journalId)?.Metadata);
    
    public Task<Result> DeleteJournalAsync(string journalId)
    {
        _entries.RemoveAll(e => e.Metadata.Id == journalId);
        return Task.FromResult(Result.Success());
    }
    
    public Task<Result> RenameJournalAsync(string journalId, string newName) => Task.FromResult(Result.Success());
    public Task<Result> UpdateJournalAsync(Journal journal) => Task.FromResult(Result.Success());
    public Task<Result> SaveInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes) => Task.FromResult(Result.Success());
    public Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId) => Task.FromResult<IReadOnlyList<JournalInkStroke>>([]);
    public Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke) => Task.FromResult(Result.Success());
    public Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId) => Task.FromResult(Result.Success());
    public Task<JournalDataSnapshot> GetSnapshotAsync() => Task.FromResult(new JournalDataSnapshot { Journals = [], DeletedJournals = [], LastModifiedUtc = DateTime.UtcNow });
    public Task MergeRemoteAsync(JournalDataSnapshot remote) => Task.CompletedTask;
}
```

Add actual test methods to `JournalFlyoutViewModelTests`:

```csharp
public class JournalFlyoutViewModelTests
{
    [Fact]
    public async Task RefreshAsync_PopulatesJournals()
    {
        var store = new FakeJournalStore();
        await store.CreateJournalAsync(new JournalCreateRequest
        {
            Name = "Alpha",
            TranslationId = "", TranslationVersionDate = "", ContentHash = "",
            BookCode = "GEN", StartChapter = 1, StartVerse = 1, EndChapter = 1, EndVerse = 31,
            Layout = new JournalLayout { TextColumnWidthDip = 600, LeftMarginDip = 80, RightMarginDip = 115, FontFamily = "Inter", FontSizeDip = 16, LineHeightDip = 24 }
        });
        var vm = new JournalFlyoutViewModel(store);

        await vm.RefreshAsync();

        Assert.Single(vm.Journals);
        Assert.Equal("Alpha", vm.Journals[0].Name);
    }

    [Fact]
    public async Task CreateJournalAsync_AddsToJournalsAndFiresActivated()
    {
        var store = new FakeJournalStore();
        var vm = new JournalFlyoutViewModel(store);
        string? activatedId = null;
        vm.JournalActivated += (_, id) => activatedId = id;

        await vm.CreateJournalAsync("Beta", "GEN", 1);

        Assert.Single(vm.Journals);
        Assert.NotNull(activatedId);
    }

    [Fact]
    public async Task DeleteJournalAsync_RemovesFromList_AndFiresDeactivatedIfWasActive()
    {
        var store = new FakeJournalStore();
        var vm = new JournalFlyoutViewModel(store);
        await vm.CreateJournalAsync("Gamma", "GEN", 1);
        var journalId = vm.Journals[0].Id;
        vm.SetActiveJournal(journalId);
        bool deactivated = false;
        vm.JournalDeactivated += (_, _) => deactivated = true;

        await vm.DeleteJournalAsync(journalId);

        Assert.Empty(vm.Journals);
        Assert.True(deactivated);
    }

    [Fact]
    public async Task ActivateJournalAsync_FiresJournalActivatedWithId()
    {
        var store = new FakeJournalStore();
        var vm = new JournalFlyoutViewModel(store);
        await vm.CreateJournalAsync("Delta", "GEN", 1);
        var journalId = vm.Journals[0].Id;
        string? activatedId = null;
        vm.JournalActivated += (_, id) => activatedId = id;

        vm.ActivateJournal(journalId);

        Assert.Equal(journalId, activatedId);
        Assert.Equal(journalId, vm.ActiveJournalId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test MyBibleApp.Journal.Tests --filter "JournalFlyoutViewModelTests"
```
Expected: compile error — `JournalFlyoutViewModel` not found.

- [ ] **Step 3: Create `MyBibleApp/ViewModels/JournalFlyoutViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using MyBibleApp.Models;
using MyBibleApp.Services;

namespace MyBibleApp.ViewModels;

public sealed class JournalFlyoutViewModel : ViewModelBase
{
    private readonly IJournalStore _journalStore;
    private string? _activeJournalId;
    private bool _hasEphemeralStrokes;

    public JournalFlyoutViewModel(IJournalStore journalStore)
    {
        _journalStore = journalStore;
    }

    public ObservableCollection<Journal> Journals { get; } = [];
    
    public string? ActiveJournalId
    {
        get => _activeJournalId;
        private set => SetField(ref _activeJournalId, value);
    }

    public bool HasEphemeralStrokes
    {
        get => _hasEphemeralStrokes;
        set => SetField(ref _hasEphemeralStrokes, value);
    }

    public event EventHandler<string>? JournalActivated;
    public event EventHandler? JournalDeactivated;

    public async Task RefreshAsync()
    {
        var all = await _journalStore.GetAllJournalsAsync();
        Journals.Clear();
        foreach (var j in all)
            Journals.Add(j);
    }

    public async Task CreateJournalAsync(string name, string bookCode, int chapter)
    {
        var request = new JournalCreateRequest
        {
            Name = name,
            TranslationId = "",
            TranslationVersionDate = "",
            ContentHash = "",
            BookCode = bookCode,
            StartChapter = chapter,
            StartVerse = 1,
            EndChapter = chapter,
            EndVerse = 999,
            Layout = new JournalLayout
            {
                TextColumnWidthDip = 600,
                LeftMarginDip = 80,
                RightMarginDip = 115,
                FontFamily = "Inter",
                FontSizeDip = 16,
                LineHeightDip = 24
            }
        };
        var result = await _journalStore.CreateJournalAsync(request);
        if (!result.IsSuccess) return;

        Journals.Add(result.Value!);
        ActivateJournal(result.Value!.Id);
    }

    public async Task DeleteJournalAsync(string journalId)
    {
        await _journalStore.DeleteJournalAsync(journalId);
        var toRemove = Journals.FirstOrDefault(j => j.Id == journalId);
        if (toRemove != null) Journals.Remove(toRemove);
        if (ActiveJournalId == journalId)
        {
            ActiveJournalId = null;
            JournalDeactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RenameJournalAsync(string journalId, string newName)
    {
        await _journalStore.RenameJournalAsync(journalId, newName);
        await RefreshAsync();
    }

    public void ActivateJournal(string journalId)
    {
        ActiveJournalId = journalId;
        JournalActivated?.Invoke(this, journalId);
    }

    public void DeactivateJournal()
    {
        ActiveJournalId = null;
        JournalDeactivated?.Invoke(this, EventArgs.Empty);
    }

    public void SetActiveJournal(string? journalId)
    {
        ActiveJournalId = journalId;
    }
}
```

Note: `ViewModelBase` must provide `SetField`. If the base class uses `RaiseAndSetIfChanged` (ReactiveUI) instead, replace `SetField` with the appropriate call — check existing ViewModels in the project for the correct pattern.

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test MyBibleApp.Journal.Tests --filter "JournalFlyoutViewModelTests"
```
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```
git add MyBibleApp/ViewModels/JournalFlyoutViewModel.cs MyBibleApp.Journal.Tests/Unit/JournalFlyoutViewModelTests.cs
git commit -m "feat: add JournalFlyoutViewModel"
```

---

## Task 6: Create JournalFlyoutView

**Files:**
- Create: `MyBibleApp/Views/JournalFlyoutView.axaml`
- Create: `MyBibleApp/Views/JournalFlyoutView.axaml.cs`

No automated test. Visual verification in Task 7 + 8 after wiring.

- [ ] **Step 1: Create `MyBibleApp/Views/JournalFlyoutView.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:MyBibleApp.ViewModels"
             x:Class="MyBibleApp.Views.JournalFlyoutView"
             x:DataType="vm:JournalFlyoutViewModel"
             Width="260">
    <Border CornerRadius="12"
            Background="{DynamicResource ThemeBackgroundBrush}"
            BorderBrush="{DynamicResource SystemBaseMediumColor}"
            BorderThickness="1"
            BoxShadow="0 4 16 0 #44000000"
            Padding="8">
        <StackPanel Spacing="4">

            <!-- New journal button -->
            <Button HorizontalAlignment="Stretch"
                    HorizontalContentAlignment="Left"
                    Classes="subtle"
                    x:Name="CreateButton"
                    Padding="8,6">
                <StackPanel Orientation="Horizontal" Spacing="6">
                    <material:MaterialIcon Kind="Plus" Width="16" Height="16" />
                    <TextBlock Text="New Journal" />
                </StackPanel>
            </Button>

            <Separator Margin="0,4" />

            <!-- Journal list -->
            <ScrollViewer MaxHeight="300">
                <ItemsControl ItemsSource="{Binding Journals}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <Grid ColumnDefinitions="Auto,*,Auto" Margin="0,1">
                                <!-- Active indicator dot -->
                                <Ellipse Grid.Column="0"
                                         Width="8" Height="8"
                                         Margin="4,0,8,0"
                                         VerticalAlignment="Center">
                                    <Ellipse.Fill>
                                        <MultiBinding Converter="{StaticResource ActiveJournalDotConverter}">
                                            <Binding Path="Id" />
                                            <Binding Path="DataContext.ActiveJournalId" RelativeSource="{RelativeSource AncestorType=UserControl}" />
                                        </MultiBinding>
                                    </Ellipse.Fill>
                                </Ellipse>
                                <!-- Journal name button -->
                                <Button Grid.Column="1"
                                        HorizontalAlignment="Stretch"
                                        HorizontalContentAlignment="Left"
                                        Classes="subtle"
                                        Command="{Binding DataContext.ActivateJournalCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                        CommandParameter="{Binding Id}"
                                        Padding="4,6">
                                    <TextBlock Text="{Binding Name}"
                                               TextTrimming="CharacterEllipsis" />
                                </Button>
                                <!-- Delete button -->
                                <Button Grid.Column="2"
                                        Classes="subtle icon-btn"
                                        x:Name="DeleteButton"
                                        Tag="{Binding Id}"
                                        Padding="4">
                                    <material:MaterialIcon Kind="TrashCanOutline" Width="14" Height="14" />
                                </Button>
                            </Grid>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>

            <!-- Save as journal (shown only when ephemeral strokes exist and no active journal) -->
            <Separator Margin="0,4"
                       IsVisible="{Binding HasEphemeralStrokes}" />
            <Button HorizontalAlignment="Stretch"
                    HorizontalContentAlignment="Left"
                    Classes="subtle"
                    IsVisible="{Binding HasEphemeralStrokes}"
                    x:Name="SaveAsButton"
                    Padding="8,6">
                <StackPanel Orientation="Horizontal" Spacing="6">
                    <material:MaterialIcon Kind="ContentSave" Width="16" Height="16" />
                    <TextBlock Text="Save as Journal…" />
                </StackPanel>
            </Button>

        </StackPanel>
    </Border>
</UserControl>
```

Note: `ActiveJournalDotConverter` is a simple value converter (see code-behind step). `ActivateJournalCommand` is an `ICommand` wrapping `ActivateJournal` — add it to `JournalFlyoutViewModel` or handle via click event in code-behind. The simplest approach is to use the code-behind for button clicks rather than commands — see Step 2.

- [ ] **Step 2: Create `MyBibleApp/Views/JournalFlyoutView.axaml.cs`**

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class JournalFlyoutView : UserControl
{
    public JournalFlyoutView()
    {
        InitializeComponent();
        
        var createButton = this.FindControl<Button>("CreateButton");
        if (createButton != null)
            createButton.Click += OnCreateClicked;

        var saveAsButton = this.FindControl<Button>("SaveAsButton");
        if (saveAsButton != null)
            saveAsButton.Click += OnSaveAsClicked;
    }

    private async void OnCreateClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not JournalFlyoutViewModel vm) return;

        // Simple name dialog — use a text input dialog. 
        // For MVP, use a hardcoded default name with a timestamp.
        // Replace with a proper input dialog once available.
        var name = $"Journal {DateTime.Now:MMM d, h:mm tt}";
        await vm.CreateJournalAsync(name, vm.CurrentBookCode, vm.CurrentChapter);
    }

    private void OnSaveAsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not JournalFlyoutViewModel vm) return;
        SaveAsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void WireDeleteButtons()
    {
        // Called after items are rendered — wires delete click for each item.
        // For simplicity, handle delete via a command on JournalFlyoutViewModel instead.
        // See AppShellView wiring in Task 7.
    }

    public event EventHandler? SaveAsRequested;
    public event EventHandler? CloseRequested;
}
```

Also update `JournalFlyoutViewModel` to expose `CurrentBookCode` and `CurrentChapter` so the flyout can pass them to `CreateJournalAsync`:

In `JournalFlyoutViewModel.cs`, add:

```csharp
public string CurrentBookCode { get; set; } = string.Empty;
public int CurrentChapter { get; set; } = 1;
```

- [ ] **Step 3: Simplify AXAML — replace Converter with code-behind approach**

The `ActiveJournalDotConverter` adds complexity. Replace the `Ellipse` binding with a simpler approach: set the dot fill in code using `DataContext` observation, or use an `Ellipse` with `Fill` bound to a property on a wrapper VM per journal item. For the POC, the simplest is to set `Opacity` of the dot based on whether `Id == ActiveJournalId`.

Replace the `Ellipse` in the AXAML with:

```xml
<Ellipse Grid.Column="0"
         Width="8" Height="8"
         Margin="4,0,8,0"
         VerticalAlignment="Center"
         Fill="{DynamicResource SystemAccentColor}">
    <Ellipse.IsVisible>
        <!-- Bind to a helper — simplest: use code-behind approach -->
        <!-- For now, always show; AppShellView or ItemTemplate can refine -->
    </Ellipse.IsVisible>
</Ellipse>
```

Actually, for the POC, skip the active indicator in AXAML and add it in a follow-up. The functional behavior (activate, create, delete, save-as) is the priority.

Remove the `Ellipse` and `ActiveJournalDotConverter` from the AXAML. The final journal list item template is:

```xml
<DataTemplate>
    <Grid ColumnDefinitions="*,Auto" Margin="0,1">
        <Button Grid.Column="0"
                HorizontalAlignment="Stretch"
                HorizontalContentAlignment="Left"
                Classes="subtle"
                Tag="{Binding Id}"
                x:Name="ActivateButton"
                Padding="8,6">
            <TextBlock Text="{Binding Name}"
                       TextTrimming="CharacterEllipsis" />
        </Button>
        <Button Grid.Column="1"
                Classes="subtle"
                Tag="{Binding Id}"
                x:Name="DeleteButton"
                Padding="4">
            <material:MaterialIcon Kind="TrashCanOutline" Width="14" Height="14" />
        </Button>
    </Grid>
</DataTemplate>
```

Wire the `ActivateButton` and `DeleteButton` clicks via event in `JournalFlyoutView.axaml.cs` using the `ItemsControl.ContainerPrepared` event or simply by handling `Tapped` on the parent `ItemsControl` and reading `Tag`.

The simpler approach for the POC: handle clicks via `AddHandler` on the `ItemsControl`:

```csharp
// In JournalFlyoutView constructor, after InitializeComponent():
var itemsControl = this.FindControl<ItemsControl>("JournalItems");
if (itemsControl != null)
{
    itemsControl.AddHandler(Button.ClickEvent, OnJournalItemButtonClicked);
}
```

```csharp
private async void OnJournalItemButtonClicked(object? sender, RoutedEventArgs e)
{
    if (e.Source is not Button btn) return;
    if (DataContext is not JournalFlyoutViewModel vm) return;
    var journalId = btn.Tag as string;
    if (journalId == null) return;

    if (btn.Name == "ActivateButton")
        vm.ActivateJournal(journalId);
    else if (btn.Name == "DeleteButton")
        await vm.DeleteJournalAsync(journalId);
}
```

Add `x:Name="JournalItems"` to the `ItemsControl` in AXAML.

- [ ] **Step 4: Verify compilation**

```
dotnet build MyBibleApp
```
Expected: 0 errors

- [ ] **Step 5: Commit**

```
git add MyBibleApp/Views/JournalFlyoutView.axaml MyBibleApp/Views/JournalFlyoutView.axaml.cs MyBibleApp/ViewModels/JournalFlyoutViewModel.cs
git commit -m "feat: add JournalFlyoutView"
```

---

## Task 7: Update AppShellView.axaml.cs — tab journal state + stroke routing + flyout

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs`

- [ ] **Step 1: Add per-tab journal state fields**

In `AppShellView.axaml.cs`, in the fields section alongside `_tabInkStates`, add:

```csharp
private readonly Dictionary<ScriptureViewModel, string?> _tabActiveJournalIds = [];
private readonly Dictionary<ScriptureViewModel, List<JournalInkStroke>> _tabEphemeralStrokes = [];
private readonly Dictionary<ScriptureViewModel, Stack<string>> _tabStrokeHistory = [];
private JournalFlyoutView? _journalFlyoutView;
private JournalFlyoutViewModel? _journalFlyoutVm;
```

- [ ] **Step 2: Initialize new dictionaries in `AddTabInternal`**

In `AddTabInternal`, after `_tabInkStates[vm] = null;`, add:

```csharp
_tabActiveJournalIds[vm] = null;
_tabEphemeralStrokes[vm] = [];
_tabStrokeHistory[vm] = new Stack<string>();
```

Also in the cleanup path when a tab is removed (find where tabs are removed from `_tabs` and the dictionaries are cleaned up), add:

```csharp
_tabActiveJournalIds.Remove(vm);
_tabEphemeralStrokes.Remove(vm);
_tabStrokeHistory.Remove(vm);
```

- [ ] **Step 3: Initialize JournalFlyoutView in the constructor**

In `AppShellView()` constructor, after the existing journal view initialization lines (where `_journalListView` and `_journalModeView` are set up), add:

```csharp
_journalFlyoutVm = new JournalFlyoutViewModel(SharedSyncRuntime.Instance.JournalStore);
_journalFlyoutView = new JournalFlyoutView
{
    DataContext = _journalFlyoutVm,
    IsVisible = false
};
_journalFlyoutVm.JournalActivated += OnJournalActivated;
_journalFlyoutVm.JournalDeactivated += OnJournalDeactivated;
```

- [ ] **Step 4: Wire MainView events in the constructor**

After `_primaryView` is initialized (find where `_primaryView` is set up in the constructor or `OnLoaded`), wire the events:

```csharp
_primaryView.JournalFlyoutRequested += OnJournalFlyoutRequested;
_primaryView.StrokeCompleted += OnStrokeCompleted;
_primaryView.StrokeUndone += OnStrokeUndone;
```

- [ ] **Step 5: Update `SelectTab` to load/restore journal strokes**

In `SelectTab`, after `_primaryView.RestoreInkState(...)`, add journal stroke restoration:

```csharp
// Restore journal strokes for the incoming tab
var journalId = _tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null;
if (journalId != null)
{
    var runtime = SharedSyncRuntime.Instance;
    var journal = await runtime.JournalStore.GetJournalAsync(journalId);
    if (journal != null)
    {
        var allStrokes = await runtime.JournalStore.GetInkStrokesAsync(journalId);
        var passageStrokes = allStrokes
            .Where(s => s.BookCode == vm.BookCode && s.ChapterNumber == vm.SelectedLookupChapter)
            .ToList();
        _primaryView.LoadJournalStrokes(passageStrokes);
        _primaryView.SetActiveJournalName(journal.Name);
        _primaryView.SetUnsavedBadgeVisible(false);
    }
}
else
{
    var ephemeral = _tabEphemeralStrokes.TryGetValue(vm, out var ep) ? ep : [];
    var passageEphemeral = ephemeral
        .Where(s => s.BookCode == vm.BookCode && s.ChapterNumber == vm.SelectedLookupChapter)
        .ToList();
    _primaryView.LoadJournalStrokes(passageEphemeral);
    var hasEphemeral = ephemeral.Count > 0;
    _primaryView.SetActiveJournalName(null);
    _primaryView.SetUnsavedBadgeVisible(hasEphemeral);
}
```

Note: `SelectTab` is currently synchronous but needs `await` here. Change `private void SelectTab(int index)` to `private async void SelectTab(int index)`.

Also add a public method for `MainView` to call `LoadJournalStrokes`:

```csharp
// In MainView.axaml.cs:
public void LoadJournalStrokes(IReadOnlyList<JournalInkStroke> strokes) =>
    _inkOverlay?.LoadJournalStrokes(strokes);
```

- [ ] **Step 6: Add stroke routing handlers**

Add these handlers to `AppShellView.axaml.cs`:

```csharp
private async void OnStrokeCompleted(object? sender, InkStrokeEventArgs e)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    var stroke = new JournalInkStroke
    {
        Id = Guid.NewGuid().ToString(),
        Points = e.Points.Select(p => new StrokePoint(p.X, p.Y)).ToList(),
        Color = $"#{e.Color.A:X2}{e.Color.R:X2}{e.Color.G:X2}{e.Color.B:X2}",
        StrokeWidth = e.StrokeWidth,
        IsHighlight = e.IsHighlight,
        BookCode = vm.BookCode,
        ChapterNumber = vm.SelectedLookupChapter,
        AnchorParagraphIndex = e.AnchorParagraphIndex,
        AnchorContentTop = e.AnchorContentTop
    };

    var journalId = _tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null;
    if (journalId != null)
    {
        await SharedSyncRuntime.Instance.JournalStore.AppendInkStrokeAsync(journalId, stroke);
        SharedSyncRuntime.Instance.SyncCoordinator.EnqueueSync();
        _tabStrokeHistory[vm].Push(stroke.Id);
    }
    else
    {
        _tabEphemeralStrokes[vm].Add(stroke);
        _tabStrokeHistory[vm].Push(stroke.Id);
        _primaryView?.SetUnsavedBadgeVisible(true);
    }
}

private async void OnStrokeUndone(object? sender, EventArgs e)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    if (!_tabStrokeHistory[vm].TryPop(out var strokeId)) return;

    var journalId = _tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null;
    if (journalId != null)
    {
        await SharedSyncRuntime.Instance.JournalStore.RemoveInkStrokeAsync(journalId, strokeId);
    }
    else
    {
        _tabEphemeralStrokes[vm].RemoveAll(s => s.Id == strokeId);
        var hasEphemeral = _tabEphemeralStrokes[vm].Count > 0;
        _primaryView?.SetUnsavedBadgeVisible(hasEphemeral);
    }
}
```

Note: `EnqueueSync()` — check `ISyncCoordinator` for the actual method name (may be `EnqueueJournalSync()` or similar).

- [ ] **Step 7: Add flyout event handlers**

```csharp
private async void OnJournalFlyoutRequested(object? sender, EventArgs e)
{
    if (_journalFlyoutView == null || _journalFlyoutVm == null) return;
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    _journalFlyoutVm.CurrentBookCode = vm.BookCode;
    _journalFlyoutVm.CurrentChapter = vm.SelectedLookupChapter;
    _journalFlyoutVm.HasEphemeralStrokes = _tabEphemeralStrokes[vm].Count > 0;
    _journalFlyoutVm.SetActiveJournal(_tabActiveJournalIds.TryGetValue(vm, out var jid) ? jid : null);

    await _journalFlyoutVm.RefreshAsync();
    _journalFlyoutView.IsVisible = true;
}

private async void OnJournalActivated(object? sender, string journalId)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    _tabActiveJournalIds[vm] = journalId;
    _tabEphemeralStrokes[vm].Clear();
    _tabStrokeHistory[vm].Clear();

    var journal = await SharedSyncRuntime.Instance.JournalStore.GetJournalAsync(journalId);
    if (journal == null) return;

    var allStrokes = await SharedSyncRuntime.Instance.JournalStore.GetInkStrokesAsync(journalId);
    var passageStrokes = allStrokes
        .Where(s => s.BookCode == vm.BookCode && s.ChapterNumber == vm.SelectedLookupChapter)
        .ToList();
    _primaryView?.LoadJournalStrokes(passageStrokes);
    _primaryView?.SetActiveJournalName(journal.Name);
    _primaryView?.SetUnsavedBadgeVisible(false);
    _primaryView?.SetJournalLayout(journal.Layout);

    _journalFlyoutView!.IsVisible = false;
}

private void OnJournalDeactivated(object? sender, EventArgs e)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];

    _tabActiveJournalIds[vm] = null;
    _tabStrokeHistory[vm].Clear();
    _primaryView?.LoadJournalStrokes([]);
    _primaryView?.SetActiveJournalName(null);
    _primaryView?.SetUnsavedBadgeVisible(false);
    _primaryView?.SetJournalLayout(null);
    _journalFlyoutView!.IsVisible = false;
}
```

Also wire the flyout's `SaveAsRequested` event when the flyout view is created:

```csharp
_journalFlyoutView.SaveAsRequested += OnSaveAsJournalRequested;
```

Add the handler:

```csharp
private async void OnSaveAsJournalRequested(object? sender, EventArgs e)
{
    if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
    var vm = _tabs[_activeTabIndex];
    if (_journalFlyoutVm == null) return;

    var name = $"Journal {DateTime.Now:MMM d, h:mm tt}";
    var ephemeral = _tabEphemeralStrokes[vm].ToList();
    
    var request = new JournalCreateRequest
    {
        Name = name,
        TranslationId = "",
        TranslationVersionDate = "",
        ContentHash = "",
        BookCode = vm.BookCode,
        StartChapter = vm.SelectedLookupChapter,
        StartVerse = 1,
        EndChapter = vm.SelectedLookupChapter,
        EndVerse = 999,
        Layout = new JournalLayout
        {
            TextColumnWidthDip = 600,
            LeftMarginDip = 80,
            RightMarginDip = 115,
            FontFamily = "Inter",
            FontSizeDip = 16,
            LineHeightDip = 24
        }
    };
    var result = await SharedSyncRuntime.Instance.JournalStore.CreateJournalAsync(request);
    if (!result.IsSuccess) return;

    var journal = result.Value!;
    await SharedSyncRuntime.Instance.JournalStore.SaveInkStrokesAsync(journal.Id, ephemeral);

    _tabActiveJournalIds[vm] = journal.Id;
    _tabEphemeralStrokes[vm].Clear();
    _tabStrokeHistory[vm].Clear();

    _primaryView?.SetActiveJournalName(journal.Name);
    _primaryView?.SetUnsavedBadgeVisible(false);
    _primaryView?.SetJournalLayout(journal.Layout);
    _journalFlyoutView!.IsVisible = false;

    SharedSyncRuntime.Instance.SyncCoordinator.EnqueueSync();
}
```

- [ ] **Step 8: Remove old journal event handlers**

Delete (or comment out) the old handlers:
- `OnJournalsRequested`
- `OnJournalListCloseRequested`
- `OnJournalSelected`
- `OnJournalModeCloseRequested`

Remove their event subscriptions in the constructor.

- [ ] **Step 9: Verify compilation**

```
dotnet build MyBibleApp
```
Expected: 0 errors (there may be warnings about unused fields for `_journalListView`, `_journalModeView` — these are removed in Task 8)

- [ ] **Step 10: Commit**

```
git add MyBibleApp/Views/AppShellView.axaml.cs
git commit -m "feat: add per-tab journal routing and flyout wiring to AppShellView"
```

---

## Task 8: Update AppShellView.axaml — swap journal views + add flyout

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml`

- [ ] **Step 1: Remove JournalListView and JournalModeView from AXAML**

In `AppShellView.axaml`, find `<local:JournalListView x:Name="JournalListView" ...>` and `<local:JournalModeView x:Name="JournalModeView" ...>` and delete both elements.

- [ ] **Step 2: Add JournalFlyoutView to the overlay layer**

In `AppShellView.axaml`, find the `Grid` or `Panel` that holds the overlay views (where `BibleReadingView`, and formerly the journal views, were placed). Add `JournalFlyoutView` in the same overlay layer:

```xml
<local:JournalFlyoutView x:Name="JournalFlyoutView"
                          IsVisible="False"
                          HorizontalAlignment="Right"
                          VerticalAlignment="Top"
                          Margin="0,50,16,0"
                          ZIndex="100" />
```

The `Margin` positions it below the toolbar. Adjust as needed after visual testing.

- [ ] **Step 3: Wire the new view in AppShellView constructor**

In `AppShellView.axaml.cs` constructor, replace the line where `_journalFlyoutView = new JournalFlyoutView { ... }` is created with finding it from the AXAML:

```csharp
_journalFlyoutView = this.FindControl<JournalFlyoutView>("JournalFlyoutView");
```

And set its DataContext:

```csharp
if (_journalFlyoutView != null)
{
    _journalFlyoutView.DataContext = _journalFlyoutVm;
    _journalFlyoutView.SaveAsRequested += OnSaveAsJournalRequested;
}
```

- [ ] **Step 4: Verify compilation**

```
dotnet build MyBibleApp
```
Expected: 0 errors

- [ ] **Step 5: Commit**

```
git add MyBibleApp/Views/AppShellView.axaml MyBibleApp/Views/AppShellView.axaml.cs
git commit -m "feat: add JournalFlyoutView to AppShellView overlay layer"
```

---

## Task 9: Delete obsolete journal code

**Files:**
- Delete: `MyBibleApp/Views/JournalModeView.axaml`
- Delete: `MyBibleApp/Views/JournalModeView.axaml.cs`
- Delete: `MyBibleApp/Views/JournalListView.axaml`
- Delete: `MyBibleApp/Views/JournalListView.axaml.cs`
- Delete: `MyBibleApp/ViewModels/JournalModeViewModel.cs`
- Delete: `MyBibleApp/ViewModels/JournalListViewModel.cs`
- Delete: `MyBibleApp/Controls/JournalInkCanvas.cs`

- [ ] **Step 1: Delete the files**

```
git rm MyBibleApp/Views/JournalModeView.axaml
git rm MyBibleApp/Views/JournalModeView.axaml.cs
git rm MyBibleApp/Views/JournalListView.axaml
git rm MyBibleApp/Views/JournalListView.axaml.cs
git rm MyBibleApp/ViewModels/JournalModeViewModel.cs
git rm MyBibleApp/ViewModels/JournalListViewModel.cs
git rm MyBibleApp/Controls/JournalInkCanvas.cs
```

- [ ] **Step 2: Remove any remaining references**

Search for usages:

```
grep -r "JournalModeView\|JournalListView\|JournalModeViewModel\|JournalListViewModel\|JournalInkCanvas\|JournalInkMode" MyBibleApp/ --include="*.cs" --include="*.axaml" -l
```

For each file found: remove `using` statements, field declarations, and `xmlns` imports that reference the deleted types. Also remove `_journalListView` and `_journalModeView` fields from `AppShellView.axaml.cs` if not already cleaned up.

- [ ] **Step 3: Verify compilation**

```
dotnet build MyBibleApp
```
Expected: 0 errors

- [ ] **Step 4: Commit**

```
git commit -m "refactor: delete obsolete JournalModeView, JournalListView, JournalInkCanvas"
```

---

## Task 10: Wire journal layout override to MainView reader

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`
- Modify: `MyBibleApp/Views/MainView.axaml` (optional)

- [ ] **Step 1: Implement `SetJournalLayout` in `MainView.axaml.cs`**

Find the `SetJournalLayout` stub from Task 4 and implement it. The layout overrides font size and column width on the reader `ListBox`. The `ListBox` is `_paragraphList`. Apply overrides programmatically:

```csharp
public void SetJournalLayout(JournalLayout? layout)
{
    if (_paragraphList == null) return;

    if (layout == null)
    {
        // Clear overrides — restore defaults
        _paragraphList.MaxWidth = double.PositiveInfinity;
        _paragraphList.FontSize = 16; // default — match app's base font size
        _paragraphList.FontFamily = new FontFamily("Inter");
    }
    else
    {
        _paragraphList.MaxWidth = layout.TextColumnWidthDip;
        _paragraphList.FontSize = layout.FontSizeDip;
        _paragraphList.FontFamily = new FontFamily(layout.FontFamily);
    }
}
```

Note: If `_paragraphList.MaxWidth` doesn't produce the intended layout constraint (because the ListBox is inside a container that controls width), find the correct parent container. Inspect the AXAML hierarchy around `ParagraphList` — it may be inside a `ScrollViewer` inside a container with its own width. Apply `MaxWidth` to the correct container element instead.

- [ ] **Step 2: Verify layout override works visually**

Run the app. Open a journal. Check that column width and font size change when the journal is activated.

```
dotnet run --project MyBibleApp.Desktop
```

- [ ] **Step 3: Commit**

```
git add MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: apply journal layout overrides to MainView reader"
```

---

## Self-Review Checklist

- [x] All spec requirements have a task: architecture routing (Task 7), data model (Tasks 1-2), UI/UX (Tasks 4-6), sync (Task 7 OnStrokeCompleted), error handling (Task 7 — journal not found falls back gracefully), testing (Tasks 1, 2, 5), deleted code (Task 9)
- [x] `JournalInkStroke.BookCode`, `ChapterNumber`, `AnchorParagraphIndex`, `AnchorContentTop` used consistently across Tasks 1-7
- [x] `AppendInkStrokeAsync` / `RemoveInkStrokeAsync` defined in Task 2, used in Task 7
- [x] `StrokeCompleted` / `StrokeUndone` defined in Task 3, surfaced in Task 4, consumed in Task 7
- [x] `LoadJournalStrokes` defined in Task 3, exposed in Task 4, called in Task 7
- [x] `SetActiveJournalName` / `SetUnsavedBadgeVisible` / `SetJournalLayout` defined in Task 4, called in Task 7
- [x] `JournalFlyoutViewModel.CurrentBookCode` / `CurrentChapter` added in Task 6 step 2, used in Task 7
- [x] Ephemeral strokes survive tab switches (stored in `_tabEphemeralStrokes` per tab)
- [x] Ephemeral strokes do NOT survive restart (in-memory only — no persistence path)
- [x] `SelectTab` awaited in Task 7 step 5
- [x] One potential issue: `SelectTab` is called from synchronous contexts; making it `async void` is safe for Avalonia UI dispatch, but callers that depend on completion may need attention. Verify no caller awaits `SelectTab`.
