using System.Collections.Generic;

namespace OpenBibleApp.Models;

public sealed class BibleBook
{
    public BibleBook(string code, string title, IReadOnlyList<BibleParagraph> paragraphs, int verseCount)
    {
        Code = code;
        Title = title;
        Paragraphs = paragraphs;
        VerseCount = verseCount;
    }

    public string Code { get; }

    public string Title { get; }

    public IReadOnlyList<BibleParagraph> Paragraphs { get; }

    public int VerseCount { get; }
}

