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
}
