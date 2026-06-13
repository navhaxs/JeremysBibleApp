using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using MyBibleApp.Controls;

namespace MyBibleApp.ViewModels;

public class BibleReadingBookEntry
{
    public string Code { get; }
    public string Name { get; }
    public IReadOnlyList<BibleReadingChapterCell> Chapters { get; }

    public IBrush LabelBackground { get; }
    public IBrush LabelForeground { get; }
    public Color ReadCellColor { get; }

    public BibleReadingBookEntry(string code, string name, int chapterCount, bool isOt, int bookIndex)
    {
        Code = code;
        Name = name;
        Chapters = Enumerable.Range(1, chapterCount)
            .Select(i => new BibleReadingChapterCell(code, i))
            .ToList();

        var (labelBg, labelFg, cellColor) = BibleBookGroups.GetGroupColors(isOt, bookIndex);
        LabelBackground = new SolidColorBrush(labelBg);
        LabelForeground = new SolidColorBrush(labelFg);
        ReadCellColor = cellColor;
    }
}
