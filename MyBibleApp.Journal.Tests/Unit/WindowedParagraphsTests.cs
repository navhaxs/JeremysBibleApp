using System.Collections.Generic;
using MyBibleApp.Helpers;
using MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class WindowedParagraphsTests
{
    private static BibleParagraph Para(int chapter, int verse) =>
        new("text", null, chapter, verse, []);

    [Fact]
    public void Build_EmptyList_ReturnsEmptyGroups()
    {
        var (groups, info) = ChapterGroupBuilder.Build([]);
        Assert.Empty(groups);
        Assert.Empty(info);
    }

    [Fact]
    public void Build_SingleChapter_OneGroup()
    {
        var p1 = Para(1, 1);
        var p2 = Para(1, 2);
        var (groups, info) = ChapterGroupBuilder.Build([p1, p2]);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal((1, 0), info[p1]);
        Assert.Equal((1, 1), info[p2]);
    }

    [Fact]
    public void Build_MultipleChapters_CorrectGroups()
    {
        var p1 = Para(1, 1);
        var p2 = Para(2, 1);
        var p3 = Para(2, 2);
        var (groups, info) = ChapterGroupBuilder.Build([p1, p2, p3]);

        Assert.Equal(2, groups.Count);
        Assert.Single(groups[0]);
        Assert.Equal(2, groups[1].Count);

        Assert.Equal((1, 0), info[p1]);
        Assert.Equal((2, 0), info[p2]);
        Assert.Equal((2, 1), info[p3]);
    }

    [Fact]
    public void Build_ShortChapters_EachGetsSeparateGroup()
    {
        var paragraphs = new List<BibleParagraph>();
        for (var ch = 1; ch <= 10; ch++)
            paragraphs.Add(Para(ch, 1));  // one paragraph per chapter

        var (groups, _) = ChapterGroupBuilder.Build(paragraphs);
        Assert.Equal(10, groups.Count);
        Assert.All(groups, g => Assert.Single(g));
    }
}
