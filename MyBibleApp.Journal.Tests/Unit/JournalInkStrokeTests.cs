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
