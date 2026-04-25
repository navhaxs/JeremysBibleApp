using System;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Snapshot of reading progress for syncing
/// </summary>
public sealed class ReadingProgressSnapshot : SyncEntity
{
    /// <summary>
    /// Current book code (e.g., "JHN")
    /// </summary>
    public string BookCode { get; set; } = string.Empty;

    /// <summary>
    /// Current chapter number
    /// </summary>
    public int Chapter { get; set; } = 1;

    /// <summary>
    /// Current verse number
    /// </summary>
    public int Verse { get; set; } = 1;

    /// <summary>
    /// Timestamp when this progress was marked
    /// </summary>
    public DateTime ProgressTimestamp { get; set; } = DateTime.UtcNow;
}


