using System.Collections.Generic;
using System.Linq;

namespace MyBibleApp.ViewModels;

public class BibleReadingBookEntry
{
    public string Code { get; }
    public string Name { get; }
    public IReadOnlyList<BibleReadingChapterCell> Chapters { get; }

    public BibleReadingBookEntry(string code, string name, int chapterCount)
    {
        Code = code;
        Name = name;
        Chapters = Enumerable.Range(1, chapterCount)
            .Select(i => new BibleReadingChapterCell(code, i))
            .ToList();
    }
}
