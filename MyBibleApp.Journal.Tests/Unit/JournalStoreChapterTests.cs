using System;
using System.IO;
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

        await _store.AppendInkStrokeAsync(journalId, new JournalInkStroke
            { Id = "gen-old", BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 });
        await _store.AppendInkStrokeAsync(journalId, new JournalInkStroke
            { Id = "rom-keep", BookCode = "ROM", ChapterNumber = 8, Color = "#FF000000", StrokeWidth = 2.5 });

        await _store.SaveInkStrokesAsync(journalId, "GEN", 1,
        [
            new JournalInkStroke { Id = "gen-new", BookCode = "GEN", ChapterNumber = 1, Color = "#FF000000", StrokeWidth = 2.5 }
        ]);

        var gen1 = await _store.GetInkStrokesAsync(journalId, "GEN", 1);
        var rom8 = await _store.GetInkStrokesAsync(journalId, "ROM", 8);

        Assert.Single(gen1);
        Assert.Equal("gen-new", gen1[0].Id);
        Assert.Single(rom8);
        Assert.Equal("rom-keep", rom8[0].Id);
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

        var strokes = new System.Collections.Generic.List<JournalInkStroke>
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
}
