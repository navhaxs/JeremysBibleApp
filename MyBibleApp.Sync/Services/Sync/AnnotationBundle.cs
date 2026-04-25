using System;
using System.Collections.Generic;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Bundle of annotations (notes and ink strokes) for a specific verse
/// </summary>
public sealed class AnnotationBundle : SyncEntity
{
    /// <summary>
    /// Book code for this annotation (e.g., "JHN")
    /// </summary>
    public string BookCode { get; set; } = string.Empty;

    /// <summary>
    /// Chapter number
    /// </summary>
    public int Chapter { get; set; }

    /// <summary>
    /// Verse number (or verse range start if applicable)
    /// </summary>
    public int Verse { get; set; }

    /// <summary>
    /// Text notes associated with this verse
    /// </summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Ink strokes drawn on this verse (serialized as JSON)
    /// </summary>
    public List<SerializedInkStroke> InkStrokes { get; set; } = [];

    /// <summary>
    /// Color tags or highlights applied to this verse
    /// </summary>
    public string? HighlightColor { get; set; }

    /// <summary>
    /// Whether this annotation is bookmarked
    /// </summary>
    public bool IsBookmarked { get; set; }
}

/// <summary>
/// Serializable representation of an ink stroke
/// </summary>
public sealed class SerializedInkStroke
{
    /// <summary>
    /// List of (X, Y) coordinate pairs
    /// </summary>
    public List<(double X, double Y)> Points { get; set; } = [];

    /// <summary>
    /// Color of the stroke (as hex string)
    /// </summary>
    public string Color { get; set; } = "#000000";

    /// <summary>
    /// Width of the stroke in pixels
    /// </summary>
    public double StrokeWidth { get; set; } = 2.0;

    /// <summary>
    /// Timestamp when the stroke was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}


