using System;
using System.Collections.Generic;

namespace MyBibleApp.Models;

public sealed class JournalDataSnapshot
{
    public List<JournalEntry> Journals { get; init; } = [];
    public List<DeletedJournalTombstone> DeletedJournals { get; init; } = [];
    public DateTime LastModifiedUtc { get; init; }
}

public sealed class DeletedJournalTombstone
{
    public string Id { get; init; } = string.Empty;
    public DateTime DeletedAtUtc { get; init; }
}

public sealed class JournalEntry
{
    public Journal Metadata { get; init; } = new();
    public List<JournalInkStroke> InkStrokes { get; init; } = [];
}

public sealed class JournalCreateRequest
{
    public string Name { get; init; } = string.Empty;
    public string TranslationId { get; init; } = string.Empty;
    public string TranslationVersionDate { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string BookCode { get; init; } = string.Empty;
    public int StartChapter { get; init; }
    public int StartVerse { get; init; }
    public int EndChapter { get; init; }
    public int EndVerse { get; init; }
    public JournalLayout Layout { get; init; } = new();
}

public sealed class JournalSummary
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public string TranslationId { get; init; } = string.Empty;
    public string BookCode { get; init; } = string.Empty;
    public int StartChapter { get; init; }
    public int StartVerse { get; init; }
    public int EndChapter { get; init; }
    public int EndVerse { get; init; }
}
