using System.Collections.Generic;
using MyBibleApp.Helpers;
using MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class ChapterAnchorMigrationTests
{
    private static BibleParagraph Para(int chapter, int verse) =>
        new("text", null, chapter, verse, []);

    private static (Dictionary<BibleParagraph, (int, int)> Info, List<BibleParagraph> All)
        BuildFixture(int chapters, int versesPerChapter)
    {
        var all = new List<BibleParagraph>();
        for (var ch = 1; ch <= chapters; ch++)
            for (var v = 1; v <= versesPerChapter; v++)
                all.Add(Para(ch, v));

        var (_, info) = ChapterGroupBuilder.Build(all);
        return (info, all);
    }

    [Fact]
    public void Migrate_StrokeWithChapterAlreadySet_PassesThrough()
    {
        var (info, all) = BuildFixture(3, 5);
        var stroke = new JournalInkStroke
        {
            Id = "s1", AnchorChapter = 2, AnchorParagraphIndex = 3, AnchorContentTop = 100
        };

        var result = InkAnchorMigrator.Migrate([stroke], info, all);

        Assert.Same(result[0], stroke);   // reference equality — no allocation
    }

    [Fact]
    public void Migrate_LegacyGlobalIndex_ConvertedToChapterLocal()
    {
        var (info, all) = BuildFixture(3, 5);   // 15 paragraphs: ch1=0-4, ch2=5-9, ch3=10-14
        // Global index 7 = chapter 2, local index 2.
        var stroke = new JournalInkStroke
        {
            Id = "s2", AnchorChapter = 0, AnchorParagraphIndex = 7, AnchorContentTop = 200
        };

        var result = InkAnchorMigrator.Migrate([stroke], info, all);

        Assert.Equal(2, result[0].AnchorChapter);
        Assert.Equal(2, result[0].AnchorParagraphIndex);
        Assert.Equal(200.0, result[0].AnchorContentTop);
    }

    [Fact]
    public void Migrate_LegacyGlobalIndexOutOfRange_PassesThrough()
    {
        var (info, all) = BuildFixture(1, 5);
        var stroke = new JournalInkStroke
        {
            Id = "s3", AnchorChapter = 0, AnchorParagraphIndex = 999
        };

        var result = InkAnchorMigrator.Migrate([stroke], info, all);
        Assert.Same(result[0], stroke);
    }

    [Fact]
    public void Migrate_EmptyList_ReturnsEmpty()
    {
        var result = InkAnchorMigrator.Migrate([], new(), []);
        Assert.Empty(result);
    }
}
