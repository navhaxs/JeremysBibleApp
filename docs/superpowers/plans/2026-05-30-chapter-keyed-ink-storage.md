# Chapter-Keyed Ink Stroke Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the flat per-journal `InkStrokes` list with a chapter-keyed dictionary so chapter loads are O(1) dict lookup instead of an O(n) full-journal scan, and add segment-distance eraser tests.

**Architecture:** `JournalEntry` gains `InkStrokesByChapter: Dictionary<string, List<JournalInkStroke>>` keyed by `"{BOOKCODE}:{chapter}"`. `JournalStore` and `IJournalStore` are updated to scope all ink operations to a chapter. `AppShellView` removes its LINQ chapter filter. A one-time migration re-buckets old flat-list journals on first load. The eraser change (`DistToSegmentSq`) is already in `InkOverlayCanvas.cs`; this plan adds tests for it.

**Tech Stack:** .NET 10, C#, System.Text.Json, xUnit, Avalonia (controls not unit-tested directly)

---

## File Map

| File | Action |
|---|---|
| `MyBibleApp/Models/JournalDataSnapshot.cs` | Modify — add `InkStrokesByChapter`, keep `InkStrokes` as nullable migration shim |
| `MyBibleApp/Services/IJournalStore.cs` | Modify — update 3 signatures, add `SaveAllInkStrokesAsync` |
| `MyBibleApp/Services/JournalStore.cs` | Modify — migration, new ink methods, `_pendingRetry` type, `RenameJournalAsync`, `UpdateJournalAsync` |
| `MyBibleApp/Views/AppShellView.axaml.cs` | Modify — 3 call sites |
| `MyBibleApp/Controls/InkOverlayCanvas.cs` | Modify — make `DistToSegmentSq` internal for testing |
| `MyBibleApp.Journal.Tests/Unit/JournalStoreChapterTests.cs` | Create — migration + chapter-scoped store tests |
| `MyBibleApp.Journal.Tests/Unit/JournalStoreAppendTests.cs` | Modify — fix broken call sites for new signatures |
| `MyBibleApp.Journal.Tests/Unit/JournalFlyoutViewModelTests.cs` | Modify — update `FakeJournalStore` stub |

---

### Task 1: Update `JournalEntry` data model

**Files:**
- Modify: `MyBibleApp/Models/JournalDataSnapshot.cs`

- [ ] **Step 1: Make the change**

Replace the `JournalEntry` class in `MyBibleApp/Models/JournalDataSnapshot.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MyBibleApp.Models;

public sealed class JournalDataSnapshot
{
    public List<JournalEntry> Journals { get; init; } = [];
    public List<DeletedJournalTombstone> DeletedJournals { get; init; } = [];
    public DateTime LastModifiedUtc { get; init; }
}

public sealed class DeletedJournalTombstone
{
    public string Id { get; init; } = string.Empty;
    public DateTime DeletedAtUtc { get; init; }
}

public sealed class JournalEntry
{
    public Journal Metadata { get; set; } = new();

    // Primary store. Key = "{BOOKCODE}:{chapter}" e.g. "GEN:1", "ROM:8", "PSA:119"
    public Dictionary<string, List<JournalInkStroke>> InkStrokesByChapter { get; set; } = new();

    // v1 migration shim. Populated by JSON deserializer when reading old format.
    // Set to null after migration so it is omitted from all subsequent writes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JournalInkStroke>? InkStrokes { get; set; }
}

public sealed class JournalCreateRequest
{
    public string Name { get; init; } = string.Empty;
    public string TranslationId { get; init; } = string.Empty;
    public string TranslationVersionDate { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string BookCode { get; init; } = string.Empty;
    public int StartChapter { get; init; }
    public int StartVerse { get; init; }
    public int EndChapter { get; init; }
    public int EndVerse { get; init; }
    public JournalLayout Layout { get; init; } = new();
}

public sealed class JournalSummary
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public string TranslationId { get; init; } = string.Empty;
    public string BookCode { get; init; } = string.Empty;
    public int StartChapter { get; init; }
    public int StartVerse { get; init; }
    public int EndChapter { get; init; }
    public int EndVerse { get; init; }
}
```

- [ ] **Step 2: Build to confirm no compilation errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: build succeeds. The old `InkStrokes = entry.InkStrokes` usages in `JournalStore.cs` still compile because `InkStrokes` still exists.

- [ ] **Step 3: Commit**

```bash
git add MyBibleApp/Models/JournalDataSnapshot.cs
git commit -m "refactor: add InkStrokesByChapter to JournalEntry with v1 migration shim"
```

---

### Task 2: Update `IJournalStore` interface + `FakeJournalStore` + existing store tests

**Files:**
- Modify: `MyBibleApp/Services/IJournalStore.cs`
- Modify: `MyBibleApp.Journal.Tests/Unit/JournalFlyoutViewModelTests.cs`
- Modify: `MyBibleApp.Journal.Tests/Unit/JournalStoreAppendTests.cs`

- [ ] **Step 1: Update the interface**

Replace `MyBibleApp/Services/IJournalStore.cs` entirely:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public interface IJournalStore
{
    /// <summary>Creates a new journal. Returns the created journal or an error.</summary>
    Task<Result<Journal>> CreateJournalAsync(JournalCreateRequest request);

    /// <summary>Gets all journals, ordered by creation date descending.</summary>
    Task<IReadOnlyList<Journal>> GetAllJournalsAsync();

    /// <summary>Gets a single journal by ID.</summary>
    Task<Journal?> GetJournalAsync(string journalId);

    /// <summary>Deletes a journal and all its ink strokes.</summary>
    Task<Result> DeleteJournalAsync(string journalId);

    /// <summary>Renames a journal.</summary>
    Task<Result> RenameJournalAsync(string journalId, string newName);

    /// <summary>Updates a journal's metadata (passage, layout, etc.).</summary>
    Task<Result> UpdateJournalAsync(Journal journal);

    /// <summary>Replaces ink strokes for one chapter of a journal.</summary>
    Task<Result> SaveInkStrokesAsync(string journalId, string bookCode, int chapter, IReadOnlyList<JournalInkStroke> strokes);

    /// <summary>
    /// Replaces all chapter buckets from a flat list. Used when converting ephemeral
    /// strokes to a saved journal — strokes may span multiple chapters.
    /// </summary>
    Task<Result> SaveAllInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes);

    /// <summary>Appends a single ink stroke. Stroke carries its own BookCode + ChapterNumber.</summary>
    Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke);

    /// <summary>Removes a single ink stroke from the specified chapter.</summary>
    Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId, string bookCode, int chapter);

    /// <summary>Loads ink strokes for one chapter of a journal.</summary>
    Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId, string bookCode, int chapter);

    /// <summary>Gets the full journal data snapshot for sync.</summary>
    Task<JournalDataSnapshot> GetSnapshotAsync();

    /// <summary>Merges remote journal data using last-write-wins per journal.</summary>
    Task MergeRemoteAsync(JournalDataSnapshot remote);
}
```

- [ ] **Step 2: Update `FakeJournalStore` in `JournalFlyoutViewModelTests.cs`**

Find the `FakeJournalStore` class (starts around line 80) and update the changed method stubs:

```csharp
public Task<Result> SaveInkStrokesAsync(string journalId, string bookCode, int chapter, IReadOnlyList<JournalInkStroke> strokes) =>
    Task.FromResult(Result.Success());

public Task<Result> SaveAllInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes) =>
    Task.FromResult(Result.Success());

public Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId, string bookCode, int chapter) =>
    Task.FromResult<IReadOnlyList<JournalInkStroke>>([]);

public Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId, string bookCode, int chapter) =>
    Task.FromResult(Result.Success());
```

- [ ] **Step 3: Update `JournalStoreAppendTests.cs` for new signatures**

The three existing tests call old signatures. Replace `JournalStoreAppendTests.cs` entirely:

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using MyBibleApp.Models;
using MyBibleApp.Services;
using Models = MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class JournalStoreAppendTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"journal_test_{Guid.NewGuid():N}");
    private readonly JournalStore _store;

    public JournalStoreAppendTests() => _store = new JournalStore(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<Models.Journal> CreateTestJournalAsync()
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
    public async Task AppendInkStrokeAsync_AddsStrokeToCorrectChapter()
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
        var strokes = await _store.GetInkStrokesAsync(journal.Id, "GEN", 1);
        Assert.Single(strokes);
        Assert.Equal(stroke.Id, strokes[0].Id);
        Assert.Equal("GEN", strokes[0].BookCode);
        Assert.Equal(1, strokes[0].ChapterNumber);
    }

    [Fact]
    public async Task AppendInkStrokeAsync_DifferentChapters_StoredInSeparateBuckets()
    {
        var journal = await CreateTestJournalAsync();
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();

        await _store.AppendInkStrokeAsync(journal.Id, new JournalInkStroke
            { Id = id1, BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 });
        await _store.AppendInkStrokeAsync(journal.Id, new JournalInkStroke
            { Id = id2, BookCode = "ROM", ChapterNumber = 8, Color = "#FF000000", StrokeWidth = 2.5 });

        var gen1 = await _store.GetInkStrokesAsync(journal.Id, "GEN", 1);
        var rom8 = await _store.GetInkStrokesAsync(journal.Id, "ROM", 8);
        Assert.Single(gen1);
        Assert.Equal(id1, gen1[0].Id);
        Assert.Single(rom8);
        Assert.Equal(id2, rom8[0].Id);
    }

    [Fact]
    public async Task RemoveInkStrokeAsync_RemovesStrokeFromCorrectChapter()
    {
        var journal = await CreateTestJournalAsync();
        var id = Guid.NewGuid().ToString();
        await _store.AppendInkStrokeAsync(journal.Id, new JournalInkStroke
            { Id = id, BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 });

        var removeResult = await _store.RemoveInkStrokeAsync(journal.Id, id, "GEN", 1);

        Assert.True(removeResult.IsSuccess);
        var strokes = await _store.GetInkStrokesAsync(journal.Id, "GEN", 1);
        Assert.Empty(strokes);
    }

    [Fact]
    public async Task AppendInkStrokeAsync_ReturnsFailure_WhenJournalNotFound()
    {
        var result = await _store.AppendInkStrokeAsync("nonexistent-id", new JournalInkStroke
            { Id = "s1", BookCode = "GEN", ChapterNumber = 1 });
        Assert.False(result.IsSuccess);
    }
}
```

- [ ] **Step 4: Run tests to confirm compilation + test failures (implementation not updated yet)**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj
```

Expected: compilation errors in `JournalStore.cs` because it still uses old signatures. **This is expected.** The interface drives the implementation in Task 3.

- [ ] **Step 5: Commit interface + test changes**

```bash
git add MyBibleApp/Services/IJournalStore.cs
git add MyBibleApp.Journal.Tests/Unit/JournalFlyoutViewModelTests.cs
git add MyBibleApp.Journal.Tests/Unit/JournalStoreAppendTests.cs
git commit -m "refactor: update IJournalStore to chapter-scoped ink API"
```

---

### Task 3: Add `ChapterKey` helper + migration + `GetInkStrokesAsync`

**Files:**
- Modify: `MyBibleApp/Services/JournalStore.cs`
- Create: `MyBibleApp.Journal.Tests/Unit/JournalStoreChapterTests.cs`

The goal of this task is to restore compilation and make migration + Get work.

- [ ] **Step 1: Write failing tests**

Create `MyBibleApp.Journal.Tests/Unit/JournalStoreChapterTests.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MyBibleApp.Models;
using MyBibleApp.Services;
using Models = MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class JournalStoreChapterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"journal_chapter_test_{Guid.NewGuid():N}");
    private readonly JournalStore _store;

    public JournalStoreChapterTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteOldFormatJournal(string journalId, string journalName)
    {
        // Simulates a journals.json written by the old app (flat inkStrokes list)
        var json = $$"""
        {
            "journals": [{
                "metadata": {
                    "id": "{{journalId}}",
                    "name": "{{journalName}}",
                    "translationId": "eng_bsb",
                    "translationVersionDate": "",
                    "bookCode": "GEN",
                    "startChapter": 1,
                    "startVerse": 1,
                    "endChapter": 1,
                    "endVerse": 31,
                    "contentHash": "",
                    "layout": { "textColumnWidthDip": 600, "leftMarginDip": 80, "rightMarginDip": 115 },
                    "createdAtUtc": "2025-01-01T00:00:00Z",
                    "lastModifiedUtc": "2025-01-01T00:00:00Z"
                },
                "inkStrokes": [
                    {
                        "id": "stroke-gen1",
                        "points": [{"x": 10.0, "y": 20.0}],
                        "color": "#FF000000",
                        "strokeWidth": 2.5,
                        "isHighlight": false,
                        "bookCode": "GEN",
                        "chapterNumber": 1,
                        "anchorParagraphIndex": 0,
                        "anchorContentTop": 100.0
                    },
                    {
                        "id": "stroke-rom8",
                        "points": [{"x": 5.0, "y": 15.0}],
                        "color": "#FF000000",
                        "strokeWidth": 2.5,
                        "isHighlight": false,
                        "bookCode": "ROM",
                        "chapterNumber": 8,
                        "anchorParagraphIndex": 2,
                        "anchorContentTop": 200.0
                    }
                ]
            }],
            "deletedJournals": [],
            "lastModifiedUtc": "2025-01-01T00:00:00Z"
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "journals.json"), json);
    }

    [Fact]
    public async Task GetInkStrokesAsync_OldFormat_MigratesAndReturnsCorrectChapter()
    {
        var journalId = "test-journal-migrate";
        WriteOldFormatJournal(journalId, "Migration Test");

        var gen1 = await _store.GetInkStrokesAsync(journalId, "GEN", 1);
        var rom8 = await _store.GetInkStrokesAsync(journalId, "ROM", 8);

        Assert.Single(gen1);
        Assert.Equal("stroke-gen1", gen1[0].Id);
        Assert.Single(rom8);
        Assert.Equal("stroke-rom8", rom8[0].Id);
    }

    [Fact]
    public async Task GetInkStrokesAsync_OldFormat_SavesNewFormatToDisk()
    {
        var journalId = "test-journal-save";
        WriteOldFormatJournal(journalId, "Save Test");

        // Trigger migration
        await _store.GetInkStrokesAsync(journalId, "GEN", 1);

        // Reload from disk — should use new format
        var freshStore = new JournalStore(_tempDir);
        var gen1 = await freshStore.GetInkStrokesAsync(journalId, "GEN", 1);
        Assert.Single(gen1);
        Assert.Equal("stroke-gen1", gen1[0].Id);

        // Old flat inkStrokes key should not appear (migration cleared it)
        var diskJson = File.ReadAllText(Path.Combine(_tempDir, "journals.json"));
        Assert.DoesNotContain("\"inkStrokes\":", diskJson);
        Assert.Contains("\"inkStrokesByChapter\":", diskJson);
    }

    [Fact]
    public async Task GetInkStrokesAsync_EmptyJournal_ReturnsEmpty()
    {
        var result = await _store.CreateJournalAsync(new JournalCreateRequest
        {
            Name = "Empty",
            TranslationId = "", TranslationVersionDate = "", ContentHash = "",
            BookCode = "GEN", StartChapter = 1, StartVerse = 1, EndChapter = 1, EndVerse = 31,
            Layout = new JournalLayout { TextColumnWidthDip = 600, LeftMarginDip = 80, RightMarginDip = 115 }
        });
        var journalId = result.Value!.Id;

        var strokes = await _store.GetInkStrokesAsync(journalId, "GEN", 1);

        Assert.Empty(strokes);
    }

    [Fact]
    public async Task GetInkStrokesAsync_WrongChapter_ReturnsEmpty()
    {
        var result = await _store.CreateJournalAsync(new JournalCreateRequest
        {
            Name = "WrongChapter",
            TranslationId = "", TranslationVersionDate = "", ContentHash = "",
            BookCode = "GEN", StartChapter = 1, StartVerse = 1, EndChapter = 1, EndVerse = 31,
            Layout = new JournalLayout { TextColumnWidthDip = 600, LeftMarginDip = 80, RightMarginDip = 115 }
        });
        var journalId = result.Value!.Id;
        await _store.AppendInkStrokeAsync(journalId, new JournalInkStroke
            { Id = "s1", BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 });

        var rom8 = await _store.GetInkStrokesAsync(journalId, "ROM", 8);

        Assert.Empty(rom8);
    }
}
```

- [ ] **Step 2: Run the tests to confirm they fail**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "ClassName~JournalStoreChapterTests"
```

Expected: compilation error — `JournalStore` still implements old interface.

- [ ] **Step 3: Add `ChapterKey` helper and update `_pendingRetry` type in `JournalStore.cs`**

At the top of `JournalStore` class, change the `_pendingRetry` declaration and add the helper:

```csharp
// Change:
private readonly Dictionary<string, IReadOnlyList<JournalInkStroke>> _pendingRetry = new();

// To:
private readonly Dictionary<(string JournalId, string ChapterKey), IReadOnlyList<JournalInkStroke>> _pendingRetry = new();

private static string ChapterKey(string bookCode, int chapter) => $"{bookCode}:{chapter}";
```

- [ ] **Step 4: Add migration to `LoadEntriesAsync`**

In `JournalStore.cs`, find `LoadEntriesAsync` (line ~473). After the `var snapshot = JsonSerializer.Deserialize<JournalDataSnapshot>(...)` line and before the `return`, add the migration block. The updated private method body should look like:

```csharp
private async Task<(List<JournalEntry> Entries, List<DeletedJournalTombstone> Tombstones)> LoadEntriesAsync()
{
    return await Task.Run(async () =>
    {
        if (!File.Exists(_filePath))
            return (new List<JournalEntry>(), new List<DeletedJournalTombstone>());

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return (new List<JournalEntry>(), new List<DeletedJournalTombstone>());

            var snapshot = JsonSerializer.Deserialize<JournalDataSnapshot>(json, JsonOptions);
            var entries = snapshot?.Journals ?? new List<JournalEntry>();
            var tombstones = snapshot?.DeletedJournals ?? new List<DeletedJournalTombstone>();

            // One-time migration: re-bucket v1 flat inkStrokes into inkStrokesByChapter
            bool dirty = false;
            foreach (var entry in entries)
            {
                if (entry.InkStrokes is { Count: > 0 } legacy && entry.InkStrokesByChapter.Count == 0)
                {
                    foreach (var s in legacy)
                    {
                        var key = ChapterKey(s.BookCode, s.ChapterNumber);
                        if (!entry.InkStrokesByChapter.TryGetValue(key, out var bucket))
                            entry.InkStrokesByChapter[key] = bucket = [];
                        bucket.Add(s);
                    }
                    entry.InkStrokes = null;
                    dirty = true;
                }
            }

            if (dirty)
                await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);

            return (entries, tombstones);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading journals: {ex.Message}");
            return (new List<JournalEntry>(), new List<DeletedJournalTombstone>());
        }
    }).ConfigureAwait(false);
}
```

Note: `LoadEntriesAsync` must now return `Task` not `Task<...>` synchronously — change `Task.Run(() =>` to `Task.Run(async () =>` since we now `await SaveEntriesAsync` inside it.

- [ ] **Step 5: Implement `GetInkStrokesAsync(journalId, bookCode, chapter)`**

Replace the old `GetInkStrokesAsync` method in `JournalStore.cs`:

```csharp
/// <inheritdoc />
public async Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId, string bookCode, int chapter)
{
    await _semaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        var chapterKey = ChapterKey(bookCode, chapter);
        if (_pendingRetry.TryGetValue((journalId, chapterKey), out var pendingStrokes))
            return pendingStrokes;

        var (entries, _) = await LoadEntriesAsync().ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
        if (entry == null) return [];

        return entry.InkStrokesByChapter.TryGetValue(chapterKey, out var list) ? list : [];
    }
    finally
    {
        _semaphore.Release();
    }
}
```

- [ ] **Step 6: Run tests**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "ClassName~JournalStoreChapterTests"
```

Expected: compilation errors still (other interface methods unimplemented). That's fine — we'll fix in subsequent tasks. For now just check this file compiles.

Actually build instead:
```
dotnet build MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj
```

Expected: compilation errors listing missing interface methods. All tests in this class will fail at runtime until Tasks 4-8 complete. That is acceptable mid-task state.

- [ ] **Step 7: Commit**

```bash
git add MyBibleApp/Services/JournalStore.cs
git add MyBibleApp.Journal.Tests/Unit/JournalStoreChapterTests.cs
git commit -m "feat: add ChapterKey helper, migration, and chapter-scoped GetInkStrokesAsync"
```

---

### Task 4: Implement `AppendInkStrokeAsync` (chapter-keyed)

**Files:**
- Modify: `MyBibleApp/Services/JournalStore.cs`

- [ ] **Step 1: Replace `AppendInkStrokeAsync` implementation**

Find `AppendInkStrokeAsync` (line ~308) and replace:

```csharp
/// <inheritdoc />
public async Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke)
{
    await _semaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
        if (entry == null)
            return Result.Failure($"Journal '{journalId}' not found.");

        var key = ChapterKey(stroke.BookCode, stroke.ChapterNumber);
        if (!entry.InkStrokesByChapter.TryGetValue(key, out var list))
            entry.InkStrokesByChapter[key] = list = [];
        list.Add(stroke);
        entry.Metadata.LastModifiedUtc = DateTime.UtcNow;
        await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
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

- [ ] **Step 2: Run the append tests**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "ClassName~JournalStoreAppendTests"
```

Expected: some pass now. Remaining failures will be from tasks not yet completed.

- [ ] **Step 3: Commit**

```bash
git add MyBibleApp/Services/JournalStore.cs
git commit -m "feat: AppendInkStrokeAsync writes to chapter bucket"
```

---

### Task 5: Implement `RemoveInkStrokeAsync` (chapter-keyed)

**Files:**
- Modify: `MyBibleApp/Services/JournalStore.cs`

- [ ] **Step 1: Replace `RemoveInkStrokeAsync` implementation**

Find `RemoveInkStrokeAsync` (line ~334) and replace:

```csharp
/// <inheritdoc />
public async Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId, string bookCode, int chapter)
{
    await _semaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
        if (entry == null)
            return Result.Failure($"Journal '{journalId}' not found.");

        var key = ChapterKey(bookCode, chapter);
        if (entry.InkStrokesByChapter.TryGetValue(key, out var list))
        {
            var removed = list.RemoveAll(s => s.Id == strokeId);
            if (removed > 0)
            {
                entry.Metadata.LastModifiedUtc = DateTime.UtcNow;
                await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
            }
        }
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

- [ ] **Step 2: Run the append + chapter tests**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "ClassName~JournalStoreAppendTests|ClassName~JournalStoreChapterTests"
```

Expected: all append tests pass. Chapter tests pass for Get/migration. Any remaining failures will be in tasks not yet done.

- [ ] **Step 3: Commit**

```bash
git add MyBibleApp/Services/JournalStore.cs
git commit -m "feat: RemoveInkStrokeAsync targets chapter bucket by bookCode+chapter"
```

---

### Task 6: Implement `SaveInkStrokesAsync` + `SaveAllInkStrokesAsync`

**Files:**
- Modify: `MyBibleApp/Services/JournalStore.cs`
- Modify: `MyBibleApp.Journal.Tests/Unit/JournalStoreChapterTests.cs`

- [ ] **Step 1: Write failing tests — add to `JournalStoreChapterTests.cs`**

Add these test methods to the existing `JournalStoreChapterTests` class:

```csharp
[Fact]
public async Task SaveInkStrokesAsync_ReplacesOnlyTargetChapterBucket()
{
    var result = await _store.CreateJournalAsync(new JournalCreateRequest
    {
        Name = "SaveTest",
        TranslationId = "", TranslationVersionDate = "", ContentHash = "",
        BookCode = "GEN", StartChapter = 1, StartVerse = 1, EndChapter = 1, EndVerse = 31,
        Layout = new JournalLayout { TextColumnWidthDip = 600, LeftMarginDip = 80, RightMarginDip = 115 }
    });
    var journalId = result.Value!.Id;

    // Set up two chapters
    await _store.AppendInkStrokeAsync(journalId, new JournalInkStroke
        { Id = "gen-old", BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 });
    await _store.AppendInkStrokeAsync(journalId, new JournalInkStroke
        { Id = "rom-keep", BookCode = "ROM", ChapterNumber = 8, Color = "#FF000000", StrokeWidth = 2.5 });

    // Replace GEN:1 only
    await _store.SaveInkStrokesAsync(journalId, "GEN", 1,
    [
        new JournalInkStroke { Id = "gen-new", BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 }
    ]);

    var gen1 = await _store.GetInkStrokesAsync(journalId, "GEN", 1);
    var rom8 = await _store.GetInkStrokesAsync(journalId, "ROM", 8);

    Assert.Single(gen1);
    Assert.Equal("gen-new", gen1[0].Id);
    Assert.Single(rom8);
    Assert.Equal("rom-keep", rom8[0].Id); // unchanged
}

[Fact]
public async Task SaveAllInkStrokesAsync_GroupsStrokesIntoChapterBuckets()
{
    var result = await _store.CreateJournalAsync(new JournalCreateRequest
    {
        Name = "BulkSave",
        TranslationId = "", TranslationVersionDate = "", ContentHash = "",
        BookCode = "GEN", StartChapter = 1, StartVerse = 1, EndChapter = 1, EndVerse = 31,
        Layout = new JournalLayout { TextColumnWidthDip = 600, LeftMarginDip = 80, RightMarginDip = 115 }
    });
    var journalId = result.Value!.Id;

    var strokes = new List<JournalInkStroke>
    {
        new() { Id = "a", BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 },
        new() { Id = "b", BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 },
        new() { Id = "c", BookCode = "ROM", ChapterNumber = 8, Color = "#FF000000", StrokeWidth = 2.5 },
    };

    await _store.SaveAllInkStrokesAsync(journalId, strokes);

    var gen1 = await _store.GetInkStrokesAsync(journalId, "GEN", 1);
    var rom8 = await _store.GetInkStrokesAsync(journalId, "ROM", 8);

    Assert.Equal(2, gen1.Count);
    Assert.Single(rom8);
    Assert.Equal("c", rom8[0].Id);
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "ClassName~JournalStoreChapterTests&MethodName~Save"
```

Expected: FAIL — `SaveInkStrokesAsync` and `SaveAllInkStrokesAsync` not yet implemented.

- [ ] **Step 3: Implement both methods in `JournalStore.cs`**

Replace the old `SaveInkStrokesAsync` and add `SaveAllInkStrokesAsync`. Note: the old method had pending-retry flush logic — that is moved in Task 7. For now, implement without retry logic:

```csharp
/// <inheritdoc />
public async Task<Result> SaveInkStrokesAsync(string journalId, string bookCode, int chapter, IReadOnlyList<JournalInkStroke> strokes)
{
    await _semaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
        if (entry == null)
            return Result.Failure("Journal not found.");

        var chapterKey = ChapterKey(bookCode, chapter);
        entry.InkStrokesByChapter[chapterKey] = strokes.ToList();
        entry.Metadata.LastModifiedUtc = DateTime.UtcNow;

        try
        {
            await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
            _pendingRetry.Remove((journalId, chapterKey));
            return Result.Success();
        }
        catch (Exception ex)
        {
            _pendingRetry[(journalId, chapterKey)] = strokes;
            return Result.Failure($"Failed to persist ink strokes (retained in memory for retry): {ex.Message}");
        }
    }
    finally
    {
        _semaphore.Release();
    }
}

/// <inheritdoc />
public async Task<Result> SaveAllInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes)
{
    await _semaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
        if (entry == null)
            return Result.Failure("Journal not found.");

        // Group flat list into chapter buckets
        entry.InkStrokesByChapter.Clear();
        foreach (var s in strokes)
        {
            var key = ChapterKey(s.BookCode, s.ChapterNumber);
            if (!entry.InkStrokesByChapter.TryGetValue(key, out var bucket))
                entry.InkStrokesByChapter[key] = bucket = [];
            bucket.Add(s);
        }
        entry.Metadata.LastModifiedUtc = DateTime.UtcNow;

        await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
        return Result.Success();
    }
    catch (Exception ex)
    {
        return Result.Failure($"Failed to persist ink strokes: {ex.Message}");
    }
    finally
    {
        _semaphore.Release();
    }
}
```

- [ ] **Step 4: Run tests**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "ClassName~JournalStoreChapterTests"
```

Expected: Save tests pass. All other chapter tests still pass.

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Services/JournalStore.cs
git add MyBibleApp.Journal.Tests/Unit/JournalStoreChapterTests.cs
git commit -m "feat: SaveInkStrokesAsync and SaveAllInkStrokesAsync with chapter buckets"
```

---

### Task 7: Fix `SaveInkStrokesAsync` pending-retry flush logic

**Files:**
- Modify: `MyBibleApp/Services/JournalStore.cs`
- Modify: `MyBibleApp.Journal.Tests/Unit/JournalStoreChapterTests.cs`

The old `SaveInkStrokesAsync` flushed pending retries for other journals. This logic was dropped in Task 6. Restore it with the new `(journalId, chapterKey)` key type.

- [ ] **Step 1: Write a failing test — add to `JournalStoreChapterTests.cs`**

This test is tricky to write at the integration level (we can't easily force a disk failure). Instead, test the observable behaviour: if `_pendingRetry` has been populated (which we can only verify indirectly), a successful save for another chapter still persists correctly. Write a simpler proxy test:

```csharp
[Fact]
public async Task SaveInkStrokesAsync_CanSaveTwoChapters_BothPersist()
{
    var result = await _store.CreateJournalAsync(new JournalCreateRequest
    {
        Name = "RetryFlush",
        TranslationId = "", TranslationVersionDate = "", ContentHash = "",
        BookCode = "GEN", StartChapter = 1, StartVerse = 1, EndChapter = 1, EndVerse = 31,
        Layout = new JournalLayout { TextColumnWidthDip = 600, LeftMarginDip = 80, RightMarginDip = 115 }
    });
    var journalId = result.Value!.Id;

    await _store.SaveInkStrokesAsync(journalId, "GEN", 1,
    [
        new JournalInkStroke { Id = "g1", BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 }
    ]);
    await _store.SaveInkStrokesAsync(journalId, "ROM", 8,
    [
        new JournalInkStroke { Id = "r1", BookCode = "ROM", ChapterNumber = 8, Color = "#FF000000", StrokeWidth = 2.5 }
    ]);

    var freshStore = new JournalStore(_tempDir);
    var gen1 = await freshStore.GetInkStrokesAsync(journalId, "GEN", 1);
    var rom8 = await freshStore.GetInkStrokesAsync(journalId, "ROM", 8);
    Assert.Single(gen1);
    Assert.Single(rom8);
}
```

- [ ] **Step 2: Run test to verify it passes (it should already)**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "MethodName~SaveInkStrokesAsync_CanSaveTwoChapters"
```

Expected: PASS — basic persistence already works.

- [ ] **Step 3: Update the pending-retry flush in `SaveInkStrokesAsync`**

In `JournalStore.cs`, update `SaveInkStrokesAsync` to flush other pending retries on a successful save:

```csharp
/// <inheritdoc />
public async Task<Result> SaveInkStrokesAsync(string journalId, string bookCode, int chapter, IReadOnlyList<JournalInkStroke> strokes)
{
    await _semaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
        if (entry == null)
            return Result.Failure("Journal not found.");

        var chapterKey = ChapterKey(bookCode, chapter);
        entry.InkStrokesByChapter[chapterKey] = strokes.ToList();
        entry.Metadata.LastModifiedUtc = DateTime.UtcNow;

        // Apply any pending retries for other (journal, chapter) combos
        foreach (var (retryKey, retryStrokes) in _pendingRetry.ToList())
        {
            if (retryKey.JournalId == journalId && retryKey.ChapterKey == chapterKey)
                continue; // current save supersedes this

            var pendingEntry = entries.FirstOrDefault(e => e.Metadata.Id == retryKey.JournalId);
            if (pendingEntry != null)
                pendingEntry.InkStrokesByChapter[retryKey.ChapterKey] = retryStrokes.ToList();
        }

        try
        {
            await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
            // Clear all pending retries that were flushed
            _pendingRetry.Remove((journalId, chapterKey));
            foreach (var key in _pendingRetry.Keys.ToList())
            {
                if (entries.Any(e => e.Metadata.Id == key.JournalId))
                    _pendingRetry.Remove(key);
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            _pendingRetry[(journalId, chapterKey)] = strokes;
            return Result.Failure($"Failed to persist ink strokes (retained in memory for retry): {ex.Message}");
        }
    }
    finally
    {
        _semaphore.Release();
    }
}
```

- [ ] **Step 4: Run all chapter + append tests**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "ClassName~JournalStoreChapterTests|ClassName~JournalStoreAppendTests"
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Services/JournalStore.cs
git add MyBibleApp.Journal.Tests/Unit/JournalStoreChapterTests.cs
git commit -m "feat: restore pending-retry flush logic with chapter-keyed _pendingRetry"
```

---

### Task 8: Update `RenameJournalAsync`, `UpdateJournalAsync`, `CreateJournalAsync`

**Files:**
- Modify: `MyBibleApp/Services/JournalStore.cs`

These methods create `new JournalEntry { Metadata = ..., InkStrokes = entry.InkStrokes }`. They must switch to `InkStrokesByChapter`.

- [ ] **Step 1: Update `RenameJournalAsync` (line ~187)**

Change:
```csharp
entries[index] = new JournalEntry { Metadata = updatedJournal, InkStrokes = entry.InkStrokes };
```
To:
```csharp
entries[index] = new JournalEntry { Metadata = updatedJournal, InkStrokesByChapter = entry.InkStrokesByChapter };
```

- [ ] **Step 2: Update `UpdateJournalAsync` (line ~213)**

Change:
```csharp
entries[index] = new JournalEntry { Metadata = journal, InkStrokes = entry.InkStrokes };
```
To:
```csharp
entries[index] = new JournalEntry { Metadata = journal, InkStrokesByChapter = entry.InkStrokesByChapter };
```

- [ ] **Step 3: Update `CreateJournalAsync` (line ~70)**

Change:
```csharp
entries.Add(new JournalEntry { Metadata = journal, InkStrokes = [] });
```
To:
```csharp
entries.Add(new JournalEntry { Metadata = journal });
```

(`InkStrokesByChapter` defaults to `new Dictionary<string, List<JournalInkStroke>>()` in the property initializer.)

- [ ] **Step 4: Build and run all journal tests**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj
```

Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Services/JournalStore.cs
git commit -m "fix: JournalEntry construction uses InkStrokesByChapter in all store methods"
```

---

### Task 9: Update `AppShellView` call sites

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs`

There are three call sites. All are in `AppShellView.axaml.cs`. The app must build cleanly after this task.

- [ ] **Step 1: Update `GetInkStrokesAsync` call sites (lines ~213 and ~914)**

There are two places that do:
```csharp
var allStrokes = await SharedSyncRuntime.Instance.JournalStore.GetInkStrokesAsync(journalId);
var passageStrokes = allStrokes
    .Where(s => s.BookCode == bookCode && s.ChapterNumber == vm.SelectedLookupChapter)
    .ToList();
_primaryView.LoadJournalStrokes(passageStrokes);
```

For each occurrence, replace with:
```csharp
var passageStrokes = (await SharedSyncRuntime.Instance.JournalStore
    .GetInkStrokesAsync(journalId, bookCode, vm.SelectedLookupChapter))
    .ToList();
_primaryView.LoadJournalStrokes(passageStrokes);
```

Where `bookCode` is `vm.SelectedLookupBook?.Code ?? vm.BookCode` at the first site and `vm.BookCode` at the second. Keep the existing variable name for `bookCode` if already defined above in scope.

- [ ] **Step 2: Update `RemoveInkStrokeAsync` call site (line ~1029, in `OnStrokeRemoved`)**

Change:
```csharp
await SharedSyncRuntime.Instance.JournalStore.RemoveInkStrokeAsync(journalId, strokeId);
```
To:
```csharp
await SharedSyncRuntime.Instance.JournalStore.RemoveInkStrokeAsync(
    journalId, strokeId, vm.BookCode, vm.SelectedLookupChapter);
```

- [ ] **Step 3: Update `SaveInkStrokesAsync` call site (line ~974, in the ephemeral-to-journal conversion)**

Change:
```csharp
await SharedSyncRuntime.Instance.JournalStore.SaveInkStrokesAsync(journal.Id, ephemeral);
```
To:
```csharp
await SharedSyncRuntime.Instance.JournalStore.SaveAllInkStrokesAsync(journal.Id, ephemeral);
```

- [ ] **Step 4: Build the full solution**

```
dotnet build OpenBibleApp.sln
```

Expected: no compilation errors.

- [ ] **Step 5: Run all tests**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Views/AppShellView.axaml.cs
git commit -m "feat: update AppShellView to use chapter-scoped ink store API"
```

---

### Task 10: Eraser segment-distance tests

**Files:**
- Modify: `MyBibleApp/Controls/InkOverlayCanvas.cs` — change `DistToSegmentSq` from `private` to `internal`
- Create: `MyBibleApp.Journal.Tests/Unit/InkEraserGeometryTests.cs`

The segment-distance implementation is already in `InkOverlayCanvas.cs` (added in the previous session). This task tests the geometry.

- [ ] **Step 1: Make `DistToSegmentSq` internal**

In `MyBibleApp/Controls/InkOverlayCanvas.cs`, change:
```csharp
private static double DistToSegmentSq(Point p, Point a, Point b)
```
to:
```csharp
internal static double DistToSegmentSq(Point p, Point a, Point b)
```

- [ ] **Step 2: Add `InternalsVisibleTo` to the main project**

In `MyBibleApp/`, create or find an assembly attributes file. The simplest place is a new `AssemblyInfo.cs`:

Check if `MyBibleApp/Properties/AssemblyInfo.cs` exists. If not, create it:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MyBibleApp.Journal.Tests")]
```

- [ ] **Step 3: Verify the test project references the main project**

Check `MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj` for a `<ProjectReference>` pointing to `MyBibleApp.csproj`. If it exists, proceed. If not, add it — but first check: existing tests in `JournalFlyoutViewModelTests.cs` use `using MyBibleApp.ViewModels` which is in the main project, so the reference already exists.

- [ ] **Step 4: Write failing tests**

Create `MyBibleApp.Journal.Tests/Unit/InkEraserGeometryTests.cs`:

```csharp
using Avalonia;
using MyBibleApp.Controls;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class InkEraserGeometryTests
{
    // InkOverlayCanvas.DistToSegmentSq is internal — accessed via InternalsVisibleTo

    [Fact]
    public void DistToSegmentSq_PointOnSegment_ReturnsZero()
    {
        // Midpoint of segment (0,0)→(10,0) is (5,0)
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(5, 0), new Point(0, 0), new Point(10, 0));
        Assert.Equal(0.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_PointPerpendicularAboveSegment_ReturnsSquaredHeight()
    {
        // Point (5, 3) is 3 units above midpoint of segment (0,0)→(10,0)
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(5, 3), new Point(0, 0), new Point(10, 0));
        Assert.Equal(9.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_PointPastEndpoint_ReturnsDistToEndpoint()
    {
        // Point (15, 0) is 5 units past endpoint (10, 0)
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(15, 0), new Point(0, 0), new Point(10, 0));
        Assert.Equal(25.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_PointPastStartpoint_ReturnsDistToStartpoint()
    {
        // Point (-3, 0) is 3 units before start (0, 0)
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(-3, 0), new Point(0, 0), new Point(10, 0));
        Assert.Equal(9.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_ZeroLengthSegment_ReturnsDistToPoint()
    {
        // Degenerate segment: A == B == (5, 5)
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(8, 5), new Point(5, 5), new Point(5, 5));
        Assert.Equal(9.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_EraserHitsMidpointOfSparseStroke()
    {
        // A stroke sampled only at ends (0,0) and (100,0).
        // Eraser at (50, 10) — 10 units above midpoint.
        // Point-only test would miss; segment test should detect dist² = 100.
        const double radiusSq = 14.0 * 14.0; // 196
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(50, 10), new Point(0, 0), new Point(100, 0));
        Assert.True(dist <= radiusSq, $"Expected dist² {dist} <= {radiusSq}");
    }
}
```

- [ ] **Step 5: Run tests**

```
dotnet test MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj --filter "ClassName~InkEraserGeometryTests"
```

Expected: all 6 pass.

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Controls/InkOverlayCanvas.cs
git add MyBibleApp/Properties/AssemblyInfo.cs
git add MyBibleApp.Journal.Tests/Unit/InkEraserGeometryTests.cs
git commit -m "test: add segment-distance eraser geometry tests"
```

---

## Final Verification

- [ ] Run full test suite

```
dotnet test OpenBibleApp.sln
```

Expected: all tests pass, no regressions.

- [ ] Build release configuration

```
dotnet build OpenBibleApp.sln -c Release
```

Expected: no warnings about unused variables from removed `InkStrokes` usages.

- [ ] Manual smoke test
  - Open app, navigate to a journal, draw strokes on one chapter
  - Switch to another chapter, draw strokes there
  - Switch back — original strokes still visible
  - Erase a stroke that was drawn with fast pen movement — no gap misses
  - Close and reopen app — strokes persist in correct chapters
  - If a `journals.json` with old `inkStrokes` format exists (from a previous build), verify migration runs silently and strokes are preserved
