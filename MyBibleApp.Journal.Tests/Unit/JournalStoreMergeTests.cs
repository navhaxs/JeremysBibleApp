using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MyBibleApp.Models;
using MyBibleApp.Services;
using Models = MyBibleApp.Models;
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

    private async Task<Models.Journal> CreateJournalAsync(string name = "Test")
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
            Metadata = new Models.Journal { Id = id, LastModifiedUtc = modifiedUtc }
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
}
