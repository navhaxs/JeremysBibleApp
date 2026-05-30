using System.Collections.Generic;
using MyBibleApp.Models;

namespace MyBibleApp.Helpers;

public static class ChapterGroupBuilder
{
    /// <summary>
    /// Groups paragraphs by StartChapter. Returns chapter groups (0-indexed list of chapter paragraphs)
    /// and a lookup from paragraph to (1-based chapter, within-chapter index).
    /// </summary>
    public static (List<List<BibleParagraph>> Groups,
                   Dictionary<BibleParagraph, (int Chapter, int LocalIndex)> Info)
        Build(IReadOnlyList<BibleParagraph> paragraphs)
    {
        var groups = new List<List<BibleParagraph>>();
        var info   = new Dictionary<BibleParagraph, (int, int)>();

        int currentChapter = -1;
        List<BibleParagraph>? currentGroup = null;

        foreach (var para in paragraphs)
        {
            if (para.StartChapter != currentChapter)
            {
                currentChapter = para.StartChapter;
                currentGroup   = [];
                groups.Add(currentGroup);
            }
            var localIndex = currentGroup!.Count;
            currentGroup.Add(para);
            info[para] = (currentChapter, localIndex);
        }

        return (groups, info);
    }
}
