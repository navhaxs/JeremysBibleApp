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

    // Tracks the rendering layout engine version at journal creation time.
    // Increment this constant whenever paragraph Y-positions can change for the same content
    // (font metrics, column width, USX parsing, scroll virtualisation logic, etc.).
    // A mismatch between stored value and CurrentVersion means AnchorContentTop values
    // on existing strokes may be stale. 0 = legacy journals created before versioning (treated as 1).
    public int LayoutEngineVersion { get; init; } = 1;
    public const int CurrentVersion = 2;

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
    public int AnchorChapter { get; init; }      // 1-based chapter; 0 = legacy global index
}

public readonly record struct StrokePoint(double X, double Y);
