using System.Collections.Generic;

namespace OpenBibleApp.Models;

public sealed record BibleParagraph(string Text, int? ChapterDropCap, IReadOnlyList<BibleFootnote> Footnotes)
{
    public bool HasChapterDropCap => ChapterDropCap.HasValue;

    public string ChapterDropCapText => ChapterDropCap?.ToString() ?? string.Empty;

    public bool HasFootnotes => Footnotes.Count > 0;

    public bool IsHeading { get; init; }

    public bool IsBodyText => !IsHeading;
}

