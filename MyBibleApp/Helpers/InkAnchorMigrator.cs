using System.Collections.Generic;
using System.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Helpers;

public static class InkAnchorMigrator
{
    /// <summary>
    /// Converts legacy strokes (AnchorChapter == 0) to chapter-local anchors.
    /// Strokes with AnchorChapter > 0 pass through unchanged.
    /// </summary>
    public static IReadOnlyList<JournalInkStroke> Migrate(
        IReadOnlyList<JournalInkStroke> strokes,
        Dictionary<BibleParagraph, (int Chapter, int LocalIndex)> paragraphInfo,
        IReadOnlyList<BibleParagraph> allParagraphs)
    {
        if (allParagraphs.Count == 0) return strokes;

        List<JournalInkStroke>? result = null;

        for (var i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            if (s.AnchorChapter != 0) { result?.Add(s); continue; }

            var globalIdx = s.AnchorParagraphIndex;
            if (globalIdx < 0 || globalIdx >= allParagraphs.Count) { result?.Add(s); continue; }

            var para = allParagraphs[globalIdx];
            if (!paragraphInfo.TryGetValue(para, out var info)) { result?.Add(s); continue; }

            result ??= strokes.Take(i).ToList();
            result.Add(new JournalInkStroke
            {
                Id                   = s.Id,
                Points               = s.Points,
                Color                = s.Color,
                StrokeWidth          = s.StrokeWidth,
                IsHighlight          = s.IsHighlight,
                BookCode             = s.BookCode,
                ChapterNumber        = s.ChapterNumber,
                AnchorChapter        = info.Chapter,
                AnchorParagraphIndex = info.LocalIndex,
                AnchorContentTop     = s.AnchorContentTop
            });
        }

        return result ?? strokes;
    }
}
