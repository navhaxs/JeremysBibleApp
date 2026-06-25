using System.Collections.Generic;

namespace MyBibleApp.Models;

public sealed record BibleParagraph(string Text, int? ChapterDropCap, int StartChapter, int StartVerse, IReadOnlyList<BibleFootnote> Footnotes)
{
    public bool HasChapterDropCap => ChapterDropCap.HasValue;

    public string ChapterDropCapText => ChapterDropCap?.ToString() ?? string.Empty;

    public bool HasFootnotes => Footnotes.Count > 0;

    public bool IsHeading { get; init; }

    public bool IsParallelReference { get; init; }

    public bool IsPoetry { get; init; }

    // 0 = q1 in USX, 1 = q1, 2 = q2, 3 = q3 (set by parser).
    public int PoetryIndentLevel { get; init; }

    // 0 = prose layout (v1 journals or non-poetry). 1/2/3 = q1/q2/q3 compact with indent.
    // Set by MainView when adding paragraphs to the windowed display list based on layout version.
    public int EffectivePoetryLevel { get; init; }

    public bool IsBodyText => !IsHeading && !IsParallelReference;

    public IList<BibleInkStroke> InkStrokes { get; init; } = [];
}

