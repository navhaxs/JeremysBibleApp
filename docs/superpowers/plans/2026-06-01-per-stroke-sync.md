# Per-Stroke Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace last-write-wins journal entry sync with per-stroke 2P-Set CRDT so strokes drawn on any device survive sync and strokes erased on any device stay erased everywhere.

**Architecture:** Add `InkStrokeTombstone` to `JournalDataSnapshot.cs` and `DeletedInkStrokes` list to `JournalEntry`. Update `RemoveInkStrokeAsync` to write a tombstone alongside removing from the live list. Replace the last-write-wins branch in `MergeRemoteAsync` with a `MergeJournalEntries` helper that unions strokes by ID and unions tombstones, excluding any live stroke whose ID appears in the merged tombstone set.

**Tech Stack:** C# / .NET, xUnit, `JournalStore` + `JournalDataSnapshot` in `MyBibleApp` project.

---

## File Map

| File | Change |
|---|---|
| `MyBibleApp/Models/JournalDataSnapshot.cs` | Add `InkStrokeTombstone` model; add `DeletedInkStrokes` field to `JournalEntry` |
| `MyBibleApp/Services/JournalStore.cs` | `RemoveInkStrokeAsync`: append tombstone; `MergeRemoteAsync`: replace last-write-wins branch with `MergeJournalEntries` helper |
| `MyBibleApp.Journal.Tests/Unit/JournalStoreMergeTests.cs` | New file — merge and tombstone propagation tests |

---

## Task 1: Add `InkStrokeTombstone` model and `DeletedInkStrokes` field

**Files:**
- Modify: `MyBibleApp/Models/JournalDataSnapshot.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/JournalStoreMergeTests.cs` (create)

- [ ] **Step 1: Write a failing JSON round-trip test**

  Create `MyBibleApp.Journal.Tests/Unit/JournalStoreMergeTests.cs`:

  ```csharp
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using MyBibleApp.Models;
  using MyBibleApp.Services;
  using Xunit;

  namespace MyBibleApp.Journal.Tests.Unit;

  public class JournalStoreMergeTests : IDisposable
  {
      private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"merge_test_{Guid.NewGuid():N}");
      private readonly JournalStore _store;

      public JournalStoreMergeTests() => _store = new JournalStore(_tempDir);

      public void Dispose()
      {
          if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
      }

      // ── helpers ──────────────────────────────────────────────────────────

      private async Task<Journal> CreateJournalAsync(string name = "Test")
      {
          var result = await _store.CreateJournalAsync(new JournalCreateRequest
          {
              Name = name,
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

      private static JournalInkStroke MakeStroke(string id, string bookCode = "GEN", int chapter = 1) => new()
      {
          Id = id,
          Points = [new StrokePoint(0, 0), new StrokePoint(10, 10)],
          Color = "#FF000000",
          StrokeWidth = 2,
          BookCode = bookCode,
          ChapterNumber = chapter
      };

      private static JournalEntry MakeEntry(string id, DateTime modifiedUtc, IEnumerable<JournalInkStroke>? strokes = null, IEnumerable<InkStrokeTombstone>? tombstones = null)
      {
          var entry = new JournalEntry
          {
              Metadata = new Journal { Id = id, LastModifiedUtc = modifiedUtc }
          };
          foreach (var stroke in strokes ?? [])
          {
              var key = $"{stroke.BookCode}:{stroke.ChapterNumber}";
              if (!entry.InkStrokesByChapter.TryGetValue(key, out var bucket))
                  entry.InkStrokesByChapter[key] = bucket = [];
              bucket.Add(stroke);
          }
          if (tombstones != null)
              entry.DeletedInkStrokes.AddRange(tombstones);
          return entry;
      }

      private static JournalDataSnapshot MakeSnapshot(params JournalEntry[] entries) => new()
      {
          Journals = entries.ToList(),
          DeletedJournals = [],
          LastModifiedUtc = DateTime.UtcNow
      };

      // ── tests ─────────────────────────────────────────────────────────────

      [Fact]
      public void InkStrokeTombstone_CanBeAddedToJournalEntry()
      {
          var entry = new JournalEntry();
          entry.DeletedInkStrokes.Add(new InkStrokeTombstone
          {
              StrokeId = "stroke-1",
              DeletedAtUtc = DateTime.UtcNow
          });

          Assert.Single(entry.DeletedInkStrokes);
          Assert.Equal("stroke-1", entry.DeletedInkStrokes[0].StrokeId);
      }
  }
  ```

- [ ] **Step 2: Run test to confirm it fails (type not found)**

  ```
  dotnet test MyBibleApp.Journal.Tests --filter "InkStrokeTombstone_CanBeAddedToJournalEntry" -v
  ```

  Expected: compile error — `InkStrokeTombstone` does not exist.

- [ ] **Step 3: Add the model and field**

  In `MyBibleApp/Models/JournalDataSnapshot.cs`, add after `DeletedJournalTombstone`:

  ```csharp
  public sealed class InkStrokeTombstone
  {
      public string StrokeId { get; init; } = string.Empty;
      public DateTime DeletedAtUtc { get; init; }
  }
  ```

  Add to `JournalEntry` (after the `InkStrokes` v1 migration shim):

  ```csharp
  public List<InkStrokeTombstone> DeletedInkStrokes { get; set; } = [];
  ```

- [ ] **Step 4: Run test to confirm it passes**

  ```
  dotnet test MyBibleApp.Journal.Tests --filter "InkStrokeTombstone_CanBeAddedToJournalEntry" -v
  ```

  Expected: PASS.

- [ ] **Step 5: Commit**

  ```bash
  git add MyBibleApp/Models/JournalDataSnapshot.cs MyBibleApp.Journal.Tests/Unit/JournalStoreMergeTests.cs
  git commit -m "feat: add InkStrokeTombstone model and DeletedInkStrokes field to JournalEntry"
  ```

---

## Task 2: `RemoveInkStrokeAsync` — write tombstone on erase

**Files:**
- Modify: `MyBibleApp/Services/JournalStore.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/JournalStoreMergeTests.cs`

- [ ] **Step 1: Write the failing test**

  Add to `JournalStoreMergeTests`:

  ```csharp
  [Fact]
  public async Task RemoveInkStroke_WritesTombstone()
  {
      var journal = await CreateJournalAsync();
      var stroke = MakeStroke("stroke-abc");
      await _store.AppendInkStrokeAsync(journal.Id, stroke);

      await _store.RemoveInkStrokeAsync(journal.Id, "stroke-abc", "GEN", 1);

      var snapshot = await _store.GetSnapshotAsync();
      var entry = snapshot.Journals.First(e => e.Metadata.Id == journal.Id);
      Assert.Contains(entry.DeletedInkStrokes, t => t.StrokeId == "stroke-abc");
  }
  ```

- [ ] **Step 2: Run test to confirm it fails**

  ```
  dotnet test MyBibleApp.Journal.Tests --filter "RemoveInkStroke_WritesTombstone" -v
  ```

  Expected: FAIL — `DeletedInkStrokes` is empty.

- [ ] **Step 3: Update `RemoveInkStrokeAsync`**

  In `MyBibleApp/Services/JournalStore.cs`, find `RemoveInkStrokeAsync` (around line 338). Replace the inner `if (entry.InkStrokesByChapter.TryGetValue(key, out var bucket))` block:

  ```csharp
  var key = ChapterKey(bookCode, chapter);
  if (entry.InkStrokesByChapter.TryGetValue(key, out var bucket))
  {
      var removed = bucket.RemoveAll(s => s.Id == strokeId);
      if (removed > 0)
      {
          entry.DeletedInkStrokes.Add(new InkStrokeTombstone
          {
              StrokeId = strokeId,
              DeletedAtUtc = DateTime.UtcNow
          });
          entry.Metadata.LastModifiedUtc = DateTime.UtcNow;
          await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
      }
  }
  ```

- [ ] **Step 4: Run test to confirm it passes**

  ```
  dotnet test MyBibleApp.Journal.Tests --filter "RemoveInkStroke_WritesTombstone" -v
  ```

  Expected: PASS.

- [ ] **Step 5: Run full test suite**

  ```
  dotnet test MyBibleApp.Journal.Tests -v
  ```

  Expected: all tests pass.

- [ ] **Step 6: Commit**

  ```bash
  git add MyBibleApp/Services/JournalStore.cs MyBibleApp.Journal.Tests/Unit/JournalStoreMergeTests.cs
  git commit -m "feat: RemoveInkStrokeAsync writes InkStrokeTombstone on erase"
  ```

---

## Task 3: `MergeRemoteAsync` — per-stroke union with tombstones

**Files:**
- Modify: `MyBibleApp/Services/JournalStore.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/JournalStoreMergeTests.cs`

- [ ] **Step 1: Write failing tests for all merge scenarios**

  Add to `JournalStoreMergeTests`:

  ```csharp
  [Fact]
  public async Task Merge_BothDevicesAddDifferentStrokes_BothSurvive()
  {
      var journal = await CreateJournalAsync();
      var id = journal.Id;
      var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

      var strokeA = MakeStroke("stroke-a");
      var strokeB = MakeStroke("stroke-b");

      var localEntry = MakeEntry(id, t, [strokeA]);
      var remoteEntry = MakeEntry(id, t.AddMinutes(1), [strokeB]);

      // Seed local store with localEntry state
      await _store.AppendInkStrokeAsync(id, strokeA);

      // Merge remote snapshot
      await _store.MergeRemoteAsync(MakeSnapshot(remoteEntry));

      var snapshot = await _store.GetSnapshotAsync();
      var entry = snapshot.Journals.First(e => e.Metadata.Id == id);
      var allStrokes = entry.InkStrokesByChapter.Values.SelectMany(x => x).ToList();

      Assert.Contains(allStrokes, s => s.Id == "stroke-a");
      Assert.Contains(allStrokes, s => s.Id == "stroke-b");
  }

  [Fact]
  public async Task Merge_RemoteTombstone_RemovesLocalStroke()
  {
      var journal = await CreateJournalAsync();
      var id = journal.Id;
      var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

      var stroke = MakeStroke("stroke-x");
      await _store.AppendInkStrokeAsync(id, stroke);

      // Remote has tombstone for stroke-x
      var remoteEntry = MakeEntry(id, t.AddMinutes(1), [], [new InkStrokeTombstone
      {
          StrokeId = "stroke-x",
          DeletedAtUtc = t.AddMinutes(1)
      }]);

      await _store.MergeRemoteAsync(MakeSnapshot(remoteEntry));

      var snapshot = await _store.GetSnapshotAsync();
      var entry = snapshot.Journals.First(e => e.Metadata.Id == id);
      var allStrokes = entry.InkStrokesByChapter.Values.SelectMany(x => x).ToList();

      Assert.DoesNotContain(allStrokes, s => s.Id == "stroke-x");
      Assert.Contains(entry.DeletedInkStrokes, t => t.StrokeId == "stroke-x");
  }

  [Fact]
  public async Task Merge_LocalTombstone_ExcludesRemoteStroke()
  {
      var journal = await CreateJournalAsync();
      var id = journal.Id;
      var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

      var stroke = MakeStroke("stroke-y");
      await _store.AppendInkStrokeAsync(id, stroke);
      await _store.RemoveInkStrokeAsync(id, "stroke-y", "GEN", 1);

      // Remote still has stroke-y (hasn't synced the erase)
      var remoteEntry = MakeEntry(id, t.AddMinutes(1), [stroke]);

      await _store.MergeRemoteAsync(MakeSnapshot(remoteEntry));

      var snapshot = await _store.GetSnapshotAsync();
      var entry = snapshot.Journals.First(e => e.Metadata.Id == id);
      var allStrokes = entry.InkStrokesByChapter.Values.SelectMany(x => x).ToList();

      Assert.DoesNotContain(allStrokes, s => s.Id == "stroke-y");
  }

  [Fact]
  public async Task Merge_DuplicateStrokeId_OnlyOneCopySurvives()
  {
      var journal = await CreateJournalAsync();
      var id = journal.Id;
      var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

      var stroke = MakeStroke("stroke-dup");
      await _store.AppendInkStrokeAsync(id, stroke);

      var remoteEntry = MakeEntry(id, t.AddMinutes(1), [stroke]);

      await _store.MergeRemoteAsync(MakeSnapshot(remoteEntry));

      var snapshot = await _store.GetSnapshotAsync();
      var entry = snapshot.Journals.First(e => e.Metadata.Id == id);
      var allStrokes = entry.InkStrokesByChapter.Values.SelectMany(x => x).ToList();

      Assert.Single(allStrokes.Where(s => s.Id == "stroke-dup"));
  }

  [Fact]
  public async Task Merge_TombstonesFromBothDevices_UnionKeptWithLatestTimestamp()
  {
      var journal = await CreateJournalAsync();
      var id = journal.Id;
      var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

      var strokeA = MakeStroke("stroke-a");
      var strokeB = MakeStroke("stroke-b");
      await _store.AppendInkStrokeAsync(id, strokeA);
      await _store.AppendInkStrokeAsync(id, strokeB);
      await _store.RemoveInkStrokeAsync(id, "stroke-a", "GEN", 1);

      var remoteEntry = MakeEntry(id, t.AddMinutes(2), [strokeA], [new InkStrokeTombstone
      {
          StrokeId = "stroke-b",
          DeletedAtUtc = t.AddMinutes(2)
      }]);

      await _store.MergeRemoteAsync(MakeSnapshot(remoteEntry));

      var snapshot = await _store.GetSnapshotAsync();
      var entry = snapshot.Journals.First(e => e.Metadata.Id == id);
      var allStrokes = entry.InkStrokesByChapter.Values.SelectMany(x => x).ToList();

      Assert.DoesNotContain(allStrokes, s => s.Id == "stroke-a");
      Assert.DoesNotContain(allStrokes, s => s.Id == "stroke-b");
      Assert.Contains(entry.DeletedInkStrokes, t => t.StrokeId == "stroke-a");
      Assert.Contains(entry.DeletedInkStrokes, t => t.StrokeId == "stroke-b");
  }
  ```

- [ ] **Step 2: Run tests to confirm they fail**

  ```
  dotnet test MyBibleApp.Journal.Tests --filter "Merge_BothDevicesAddDifferentStrokes_BothSurvive|Merge_RemoteTombstone_RemovesLocalStroke|Merge_LocalTombstone_ExcludesRemoteStroke|Merge_DuplicateStrokeId_OnlyOneCopySurvives|Merge_TombstonesFromBothDevices_UnionKeptWithLatestTimestamp" -v
  ```

  Expected: most FAIL (current merge drops one entry's strokes on conflict).

- [ ] **Step 3: Add `MergeJournalEntries` helper to `JournalStore`**

  In `MyBibleApp/Services/JournalStore.cs`, add this private static method before `LoadEntriesAsync`:

  ```csharp
  private static JournalEntry MergeJournalEntries(JournalEntry local, JournalEntry remote)
  {
      // 1. Union stroke tombstones — keep latest DeletedAtUtc per StrokeId
      var mergedStrokeTombstones = local.DeletedInkStrokes.ToDictionary(t => t.StrokeId);
      foreach (var rt in remote.DeletedInkStrokes)
      {
          if (!mergedStrokeTombstones.TryGetValue(rt.StrokeId, out var existing) || rt.DeletedAtUtc > existing.DeletedAtUtc)
              mergedStrokeTombstones[rt.StrokeId] = rt;
      }
      var tombstonedIds = mergedStrokeTombstones.Keys.ToHashSet();

      // 2. Union live strokes from both entries — dedup by Id, exclude tombstoned
      var mergedStrokes = local.InkStrokesByChapter.Values
          .Concat(remote.InkStrokesByChapter.Values)
          .SelectMany(list => list)
          .GroupBy(s => s.Id)
          .Select(g => g.First())
          .Where(s => !tombstonedIds.Contains(s.Id))
          .ToList();

      // 3. Re-bucket by "{BookCode}:{ChapterNumber}"
      var mergedByChapter = mergedStrokes
          .GroupBy(s => $"{s.BookCode}:{s.ChapterNumber}")
          .ToDictionary(g => g.Key, g => g.ToList());

      // 4. Keep newer Metadata
      var winnerMetadata = remote.Metadata.LastModifiedUtc > local.Metadata.LastModifiedUtc
          ? remote.Metadata
          : local.Metadata;

      return new JournalEntry
      {
          Metadata = winnerMetadata,
          InkStrokesByChapter = mergedByChapter,
          DeletedInkStrokes = mergedStrokeTombstones.Values.ToList()
      };
  }
  ```

- [ ] **Step 4: Replace last-write-wins branch in `MergeRemoteAsync`**

  In `MergeRemoteAsync`, replace:

  ```csharp
  if (localById.TryGetValue(id, out var localEntry))
  {
      // Both local and remote have this journal — keep the one with later LastModifiedUtc
      merged.Add(remoteEntry.Metadata.LastModifiedUtc > localEntry.Metadata.LastModifiedUtc
          ? remoteEntry
          : localEntry);
  }
  ```

  With:

  ```csharp
  if (localById.TryGetValue(id, out var localEntry))
  {
      merged.Add(MergeJournalEntries(localEntry, remoteEntry));
  }
  ```

- [ ] **Step 5: Run the new tests to confirm they pass**

  ```
  dotnet test MyBibleApp.Journal.Tests --filter "Merge_BothDevicesAddDifferentStrokes_BothSurvive|Merge_RemoteTombstone_RemovesLocalStroke|Merge_LocalTombstone_ExcludesRemoteStroke|Merge_DuplicateStrokeId_OnlyOneCopySurvives|Merge_TombstonesFromBothDevices_UnionKeptWithLatestTimestamp" -v
  ```

  Expected: all PASS.

- [ ] **Step 6: Run full test suite**

  ```
  dotnet test MyBibleApp.Journal.Tests -v
  ```

  Expected: all tests pass.

- [ ] **Step 7: Build the full solution**

  ```
  dotnet build OpenBibleApp.sln
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Commit**

  ```bash
  git add MyBibleApp/Services/JournalStore.cs MyBibleApp.Journal.Tests/Unit/JournalStoreMergeTests.cs
  git commit -m "feat: MergeRemoteAsync uses per-stroke 2P-Set union instead of last-write-wins"
  ```

---

## Self-Review

**Spec coverage:**
- ✅ `InkStrokeTombstone` model — Task 1
- ✅ `DeletedInkStrokes` on `JournalEntry` — Task 1
- ✅ `RemoveInkStrokeAsync` writes tombstone — Task 2
- ✅ Merge: both-device strokes union — Task 3 (`Merge_BothDevicesAddDifferentStrokes_BothSurvive`)
- ✅ Merge: remote tombstone removes local stroke — Task 3 (`Merge_RemoteTombstone_RemovesLocalStroke`)
- ✅ Merge: local tombstone excludes remote stroke — Task 3 (`Merge_LocalTombstone_ExcludesRemoteStroke`)
- ✅ Merge: duplicate stroke IDs — Task 3 (`Merge_DuplicateStrokeId_OnlyOneCopySurvives`)
- ✅ Merge: tombstone union from both devices — Task 3 (`Merge_TombstonesFromBothDevices_UnionKeptWithLatestTimestamp`)
- ✅ Backwards compatible JSON (empty list default) — Task 1 model default
- ✅ No changes to `SyncCoordinator`, `JournalSyncProviderAdapter`, `IJournalSyncProvider` — confirmed by file map

**Placeholder scan:** None found.

**Type consistency:**
- `InkStrokeTombstone.StrokeId` — defined Task 1, used Task 2 (`t.StrokeId`), used Task 3 (`mergedStrokeTombstones[rt.StrokeId]`) ✓
- `JournalEntry.DeletedInkStrokes` — defined Task 1, written Task 2, merged Task 3 ✓
- `MergeJournalEntries` — defined and called Task 3 ✓
- `MakeStroke` / `MakeEntry` / `MakeSnapshot` helpers — defined once (Task 1), reused across all test steps ✓
