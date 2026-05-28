using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MyBibleApp.Models;

public sealed class Journal
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TranslationId { get; init; } = string.Empty;
    public string TranslationVersionDate { get; init; } = string.Empty;
    public string BookCode { get; init; } = string.Empty;
    public int StartChapter { get; init; }
    public int StartVerse { get; init; }
    public int EndChapter { get; init; }
    public int EndVerse { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public JournalLayout Layout { get; init; } = new();
    public DateTime CreatedAtUtc { get; init; }
    public DateTime LastModifiedUtc { get; set; }
}

public sealed class JournalLayout
{
    public double TextColumnWidthDip { get; init; }
    public double LeftMarginDip { get; init; }
    public double RightMarginDip { get; init; }
    public string FontFamily { get; init; } = string.Empty;
    public double FontSizeDip { get; init; }
    public double LineHeightDip { get; init; }

    [JsonIgnore]
    public double TotalWidthDip => LeftMarginDip + TextColumnWidthDip + RightMarginDip;
}

public sealed class JournalInkStroke
{
    public string Id { get; init; } = string.Empty;
    public IReadOnlyList<StrokePoint> Points { get; init; } = [];
    public string Color { get; init; } = string.Empty;
    public double StrokeWidth { get; init; }
    public bool IsHighlight { get; init; }
    public string BookCode { get; init; } = string.Empty;
    public int ChapterNumber { get; init; }
    public int AnchorParagraphIndex { get; init; } = -1;
    public double AnchorContentTop { get; init; }
}

public readonly record struct StrokePoint(double X, double Y);
