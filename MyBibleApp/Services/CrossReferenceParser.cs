using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MyBibleApp.Services;

/// <summary>
/// Parses a USX-style parallel-reference string (e.g. "(Psalms 75:1–10)" or
/// "(Genesis 1:1–2; Hebrews 11:1–3)") into a flat list of plain-text and
/// navigable reference segments.
/// </summary>
public static class CrossReferenceParser
{
    // Inverted from books.json book_names_english.
    // Sorted longest-first so multi-word names ("Song of Songs") win over shorter prefixes.
    private static readonly (string Name, string Code)[] KnownBooks;

    private static readonly Regex ChapterVerseRegex =
        new(@"^(\d+):(\d+)", RegexOptions.Compiled);

    static CrossReferenceParser()
    {
        (string Name, string Code)[] raw =
        [
            ("Genesis", "gen"), ("Exodus", "exo"), ("Leviticus", "lev"),
            ("Numbers", "num"), ("Deuteronomy", "deu"), ("Joshua", "jos"),
            ("Judges", "jdg"), ("Ruth", "rut"), ("1 Samuel", "1sa"),
            ("2 Samuel", "2sa"), ("1 Kings", "1ki"), ("2 Kings", "2ki"),
            ("1 Chronicles", "1ch"), ("2 Chronicles", "2ch"), ("Ezra", "ezr"),
            ("Nehemiah", "neh"), ("Esther", "est"), ("Job", "job"),
            ("Psalms", "psa"), ("Psalm", "psa"),
            ("Proverbs", "pro"), ("Ecclesiastes", "ecc"), ("Song of Songs", "sng"),
            ("Isaiah", "isa"), ("Jeremiah", "jer"), ("Lamentations", "lam"),
            ("Ezekiel", "ezk"), ("Daniel", "dan"), ("Hosea", "hos"),
            ("Joel", "jol"), ("Amos", "amo"), ("Obadiah", "oba"),
            ("Jonah", "jon"), ("Micah", "mic"), ("Nahum", "nam"),
            ("Habakkuk", "hab"), ("Zephaniah", "zep"), ("Haggai", "hag"),
            ("Zechariah", "zec"), ("Malachi", "mal"), ("Matthew", "mat"),
            ("Mark", "mrk"), ("Luke", "luk"), ("John", "jhn"),
            ("Acts", "act"), ("Romans", "rom"), ("1 Corinthians", "1co"),
            ("2 Corinthians", "2co"), ("Galatians", "gal"), ("Ephesians", "eph"),
            ("Philippians", "php"), ("Colossians", "col"), ("1 Thessalonians", "1th"),
            ("2 Thessalonians", "2th"), ("1 Timothy", "1ti"), ("2 Timothy", "2ti"),
            ("Titus", "tit"), ("Philemon", "phm"), ("Hebrews", "heb"),
            ("James", "jas"), ("1 Peter", "1pe"), ("2 Peter", "2pe"),
            ("1 John", "1jn"), ("2 John", "2jn"), ("3 John", "3jn"),
            ("Jude", "jud"), ("Revelation", "rev"),
        ];
        KnownBooks = [..raw.OrderByDescending(x => x.Name.Length)];
    }

    /// <summary>
    /// Parses a parallel-reference string into a flat sequence of segments.
    /// Recognised book references become <see cref="ReferenceSegment"/>;
    /// everything else (parentheses, semicolons, unrecognised text) becomes
    /// <see cref="TextSegment"/>.
    /// </summary>
    public static IReadOnlyList<CrossReferenceSegment> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var result = new List<CrossReferenceSegment>();
        var trimmed = text.Trim();

        // Detect and preserve outer parentheses as text segments.
        var hasParens = trimmed.StartsWith('(') && trimmed.EndsWith(')');
        var inner = hasParens ? trimmed[1..^1] : trimmed;

        if (hasParens) result.Add(new TextSegment("("));

        // Regex.Split with a capture group keeps delimiters in the array.
        // e.g. "Gen 1:1; Heb 11:1" → ["Gen 1:1", "; ", "Heb 11:1"]
        var parts = Regex.Split(inner, @"([;,]\s*)");

        for (var i = 0; i < parts.Length; i += 2)
        {
            // Add delimiter that preceded this chunk ("; ", ", ", etc.).
            if (i > 0)
                result.Add(new TextSegment(parts[i - 1]));

            var chunk = parts[i].Trim();
            if (string.IsNullOrEmpty(chunk)) continue;

            var seg = TryParseReference(chunk);
            // Preserve original spacing from parts[i] for TextSegment fallback.
            result.Add(seg ?? (CrossReferenceSegment)new TextSegment(parts[i]));
        }

        if (hasParens) result.Add(new TextSegment(")"));

        return result;
    }

    private static ReferenceSegment? TryParseReference(string text)
    {
        foreach (var (name, code) in KnownBooks)
        {
            // text must be longer than the name (needs " C:V" after it).
            if (text.Length <= name.Length) continue;
            if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (text[name.Length] != ' ') continue;

            var rest = text[(name.Length + 1)..].TrimStart();
            var match = ChapterVerseRegex.Match(rest);
            if (!match.Success) continue;

            return new ReferenceSegment(
                Display:  text,
                BookCode: code,
                Chapter:  int.Parse(match.Groups[1].Value),
                Verse:    int.Parse(match.Groups[2].Value));
        }

        return null;
    }
}

/// <summary>Base type for cross-reference text segments.</summary>
public abstract record CrossReferenceSegment;

/// <summary>Plain text — parenthesis, semicolon, or unrecognised chunk.</summary>
public sealed record TextSegment(string Text) : CrossReferenceSegment;

/// <summary>A navigable Bible reference (book, chapter, opening verse).</summary>
public sealed record ReferenceSegment(
    string Display,
    string BookCode,
    int    Chapter,
    int    Verse) : CrossReferenceSegment;
