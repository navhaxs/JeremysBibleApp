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
