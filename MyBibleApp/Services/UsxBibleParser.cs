using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class UsxBibleParser
{
    private static readonly IReadOnlyDictionary<char, char> SuperscriptDigitMap = new Dictionary<char, char>
    {
        ['0'] = '\u2070',
        ['1'] = '\u00B9',
        ['2'] = '\u00B2',
        ['3'] = '\u00B3',
        ['4'] = '\u2074',
        ['5'] = '\u2075',
        ['6'] = '\u2076',
        ['7'] = '\u2077',
        ['8'] = '\u2078',
        ['9'] = '\u2079'
    };

    public BibleBook Parse(XDocument document)
    {
        var root = document.Root ?? throw new InvalidOperationException("USX document does not have a root element.");
        var bookElement = root.Elements().FirstOrDefault(x => x.Name.LocalName == "book");

        var code = bookElement?.Attribute("code")?.Value ?? "UNK";
        var title = CollapseWhitespace(bookElement?.Value) ?? code;

        var paragraphs = new List<BibleParagraph>();
        var currentChapter = 1;
        var chapterDropCapPending = true;
        var verseCount = 0;

        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName == "chapter" && element.Attribute("number") is not null)
            {
                if (int.TryParse(element.Attribute("number")?.Value, out var parsedChapter))
                {
                    currentChapter = parsedChapter;
                    chapterDropCapPending = true;
                }

                continue;
            }

            if (element.Name.LocalName != "para")
            {
                continue;
            }

            var style = element.Attribute("style")?.Value;
            if (IsNonReadingStyle(style))
            {
                continue;
            }

            var paragraph = BuildParagraph(element, ref verseCount);
            if (string.IsNullOrWhiteSpace(paragraph.Text))
            {
                continue;
            }

            paragraphs.Add(new BibleParagraph(paragraph.Text, chapterDropCapPending ? currentChapter : null, paragraph.Footnotes)
            {
                IsHeading = IsHeadingStyle(style),
                InkStrokes = new List<BibleInkStroke>()
            });
            chapterDropCapPending = false;
        }

        return new BibleBook(code, title, paragraphs, verseCount);
    }

    private static string CollapseWhitespace(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static ParsedParagraph BuildParagraph(XElement paragraphElement, ref int verseCount)
    {
        var builder = new StringBuilder();
        var footnotes = new List<BibleFootnote>();

        foreach (var node in paragraphElement.Nodes())
        {
            AppendNode(node, builder, footnotes, ref verseCount);
        }

        return new ParsedParagraph(builder.ToString(), footnotes);
    }

    private static void AppendNode(XNode node, StringBuilder paragraphBuilder, List<BibleFootnote> footnotes, ref int verseCount)
    {
        if (node is XText textNode)
        {
            AppendText(paragraphBuilder, CollapseWhitespace(textNode.Value));
            return;
        }

        if (node is not XElement element)
        {
            return;
        }

        if (element.Name.LocalName == "verse")
        {
            // USX verse end markers use the eid attribute and should not emit text.
            if (element.Attribute("eid") is not null)
            {
                return;
            }

            var verseNumber = element.Attribute("number")?.Value;
            if (string.IsNullOrWhiteSpace(verseNumber))
            {
                return;
            }

            verseCount++;
            paragraphBuilder.Append(ToSuperscript(verseNumber));
            return;
        }

        if (element.Name.LocalName == "note")
        {
            var footnoteText = ExtractFootnoteText(element);
            if (string.IsNullOrWhiteSpace(footnoteText))
            {
                return;
            }

            var marker = ToSuperscript((footnotes.Count + 1).ToString());
            footnotes.Add(new BibleFootnote(marker, footnoteText));
            paragraphBuilder.Append(marker);
            return;
        }

        foreach (var child in element.Nodes())
        {
            AppendNode(child, paragraphBuilder, footnotes, ref verseCount);
        }
    }

    private static void AppendText(StringBuilder builder, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]) && !IsSuperscript(builder[^1]) && !IsClosingPunctuation(text[0]))
        {
            builder.Append(' ');
        }

        builder.Append(text);
    }

    private static bool IsClosingPunctuation(char value)
    {
        return value is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '"' or '\'';
    }

    private static bool IsHeadingStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }

        return style.StartsWith("s", StringComparison.OrdinalIgnoreCase)
            || style is "d" or "r" or "mr" or "ms";
    }

    private static bool IsNonReadingStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }

        // USX front matter and navigation metadata: not body-reading content.
        return style.Equals("h", StringComparison.OrdinalIgnoreCase)
            || style.StartsWith("toc", StringComparison.OrdinalIgnoreCase)
            || style.StartsWith("mt", StringComparison.OrdinalIgnoreCase)
            || style.StartsWith("imt", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractFootnoteText(XElement noteElement)
    {
        var ftChunks = noteElement
            .Descendants()
            .Where(x => x.Name.LocalName == "char" && x.Attribute("style")?.Value == "ft")
            .Select(x => CollapseWhitespace(x.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (ftChunks.Count > 0)
        {
            return string.Join(' ', ftChunks);
        }

        return CollapseWhitespace(noteElement.Value);
    }

    private static bool IsSuperscript(char value)
    {
        return value is '\u2070' or '\u00B9' or '\u00B2' or '\u00B3' or '\u2074' or '\u2075' or '\u2076' or '\u2077' or '\u2078' or '\u2079';
    }

    private static string ToSuperscript(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            builder.Append(SuperscriptDigitMap.TryGetValue(character, out var superscript) ? superscript : character);
        }

        return builder.ToString();
    }

    private sealed record ParsedParagraph(string Text, IReadOnlyList<BibleFootnote> Footnotes);
}

