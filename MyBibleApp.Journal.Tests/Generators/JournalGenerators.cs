using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using MyBibleApp.Models;

namespace MyBibleApp.Journal.Tests.Generators;

/// <summary>
/// FsCheck Arbitrary instances for Journal-related types.
/// </summary>
public static class JournalGenerators
{
    private static readonly string[] BookCodes = ["GEN", "EXO", "PSA", "JHN", "ROM"];
    private static readonly string[] FontFamilies = ["Segoe UI", "Arial", "Times New Roman", "Noto Sans"];
    private static readonly string[] TranslationIds = ["eng_bsb", "eng_kjv", "eng_esv", "eng_niv"];

    /// <summary>
    /// Generates a valid journal name (1-100 alphanumeric characters).
    /// </summary>
    public static Gen<string> JournalNameGen()
    {
        var chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ".ToCharArray();
        return from len in Gen.Choose(1, 100)
               from charArray in Gen.ArrayOf(Gen.Elements(chars), len)
               let name = new string(charArray).Trim()
               where name.Length >= 1 && name.Length <= 100
               select name;
    }

    /// <summary>
    /// Generates a valid JournalLayout with reasonable dimensions.
    /// </summary>
    public static Gen<JournalLayout> LayoutGen() =>
        from columnWidth in Gen.Choose(100, 800)
        from leftMargin in Gen.Choose(50, 200)
        from rightMargin in Gen.Choose(50, 200)
        from fontFamily in Gen.Elements(FontFamilies)
        from fontSize in Gen.Choose(8, 72)
        from lineHeight in Gen.Choose(12, 96)
        select new JournalLayout
        {
            TextColumnWidthDip = columnWidth,
            LeftMarginDip = leftMargin,
            RightMarginDip = rightMargin,
            FontFamily = fontFamily,
            FontSizeDip = fontSize,
            LineHeightDip = lineHeight
        };

    /// <summary>
    /// Generates a valid JournalCreateRequest.
    /// </summary>
    public static Arbitrary<JournalCreateRequest> CreateRequest() =>
        Arb.From(CreateRequestGen());

    /// <summary>
    /// Gen for JournalCreateRequest.
    /// </summary>
    public static Gen<JournalCreateRequest> CreateRequestGen() =>
        from name in JournalNameGen()
        from translationId in Gen.Elements(TranslationIds)
        from bookCode in Gen.Elements(BookCodes)
        from startChapter in Gen.Choose(1, 150)
        from endChapter in Gen.Choose(1, 150)
        from startVerse in Gen.Choose(1, 50)
        from endVerse in Gen.Choose(1, 50)
        from layout in LayoutGen()
        select new JournalCreateRequest
        {
            Name = name,
            TranslationId = translationId,
            BookCode = bookCode,
            StartChapter = Math.Min(startChapter, endChapter),
            StartVerse = startVerse,
            EndChapter = Math.Max(startChapter, endChapter),
            EndVerse = endVerse,
            Layout = layout
        };

    /// <summary>
    /// Generates a valid Journal with all fields populated.
    /// </summary>
    public static Arbitrary<Models.Journal> Journal() =>
        Arb.From(JournalGen());

    /// <summary>
    /// Gen for Journal.
    /// </summary>
    public static Gen<Models.Journal> JournalGen() =>
        from request in CreateRequestGen()
        from year in Gen.Choose(2020, 2025)
        from month in Gen.Choose(1, 12)
        from day in Gen.Choose(1, 28)
        from dayOfYear in Gen.Choose(1, 365)
        from modifiedOffset in Gen.Choose(0, 1000)
        select new Models.Journal
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            TranslationId = request.TranslationId,
            TranslationVersionDate = $"{year}-{month:D2}-{day:D2}",
            BookCode = request.BookCode,
            StartChapter = request.StartChapter,
            StartVerse = request.StartVerse,
            EndChapter = request.EndChapter,
            EndVerse = request.EndVerse,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Layout = request.Layout,
            CreatedAtUtc = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(dayOfYear - 1),
            LastModifiedUtc = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(dayOfYear - 1).AddMinutes(modifiedOffset)
        };

    /// <summary>
    /// Generates a valid JournalSummary.
    /// </summary>
    public static Arbitrary<JournalSummary> Summary() =>
        Arb.From(
            from journal in JournalGen()
            select new JournalSummary
            {
                Id = journal.Id,
                Name = journal.Name,
                CreatedAtUtc = journal.CreatedAtUtc,
                TranslationId = journal.TranslationId,
                BookCode = journal.BookCode,
                StartChapter = journal.StartChapter,
                StartVerse = journal.StartVerse,
                EndChapter = journal.EndChapter,
                EndVerse = journal.EndVerse
            });

    /// <summary>
    /// Generates a valid JournalDataSnapshot with 0-5 journal entries.
    /// </summary>
    public static Arbitrary<JournalDataSnapshot> DataSnapshot() =>
        Arb.From(DataSnapshotGen());

    /// <summary>
    /// Gen for JournalDataSnapshot.
    /// </summary>
    public static Gen<JournalDataSnapshot> DataSnapshotGen() =>
        from count in Gen.Choose(0, 5)
        from entries in Gen.ListOf(JournalEntryGen(), count)
        from dayOfYear in Gen.Choose(1, 365)
        select new JournalDataSnapshot
        {
            Journals = entries.ToList(),
            LastModifiedUtc = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(dayOfYear - 1)
        };

    /// <summary>
    /// Generates a valid JournalEntry (metadata + ink strokes).
    /// </summary>
    public static Arbitrary<JournalEntry> JournalEntry() =>
        Arb.From(JournalEntryGen());

    /// <summary>
    /// Gen for JournalEntry.
    /// </summary>
    public static Gen<Models.JournalEntry> JournalEntryGen() =>
        from journal in JournalGen()
        from strokeCount in Gen.Choose(0, 10)
        from strokes in Gen.ListOf(InkStrokeGenerators.InkStrokeGen(), strokeCount)
        select new Models.JournalEntry
        {
            Metadata = journal,
            InkStrokes = strokes.ToList()
        };
}
