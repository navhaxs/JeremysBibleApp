using System.Collections.Generic;
using System.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Helpers;

public static class InkAnchorMigrator
{
    /// <summary>
    /// Migrates strokes from the given layout engine version to the current version.
    /// Currently handles: AnchorChapter == 0 → chapter-local anchor (format migration, any version).
    /// Add new cases here when <see cref="JournalLayout.CurrentVersion"/> is incremented and
    /// the new engine produces different paragraph Y-positions for the same content.
    /// </summary>
    public static IReadOnlyList<JournalInkStroke> Migrate(
        IReadOnlyList<JournalInkStroke> strokes,
        Dictionary<BibleParagraph, (int Chapter, int LocalIndex)> paragraphInfo,
        IReadOnlyList<BibleParagraph> allParagraphs,
        int layoutEngineVersion = JournalLayout.CurrentVersion)
    {
        if (allParagraphs.Count == 0) return strokes;

        // Normalise missing/pre-versioning value: treat 0 as 1.
        var fromVersion = layoutEngineVersion > 0 ? layoutEngineVersion : 1;

        var result = MigrateGlobalAnchors(strokes, paragraphInfo, allParagraphs);

        // Version-specific layout migrations. Add a new case here each time
        // JournalLayout.CurrentVersion is incremented and paragraph Y-positions change.
        // Chain: fromVersion=1 → CurrentVersion=2 would apply step 1→2 here, etc.
        // (No steps needed while CurrentVersion == 1.)
        _ = fromVersion;

        return result;
    }

    // Converts legacy strokes (AnchorChapter == 0) to chapter-local anchors.
    // Format migration — version-independent.
    private static IReadOnlyList<JournalInkStroke> MigrateGlobalAnchors(
        IReadOnlyList<JournalInkStroke> strokes,
        Dictionary<BibleParagraph, (int Chapter, int LocalIndex)> paragraphInfo,
        IReadOnlyList<BibleParagraph> allParagraphs)
    {
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
