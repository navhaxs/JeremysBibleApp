using System.Collections.Generic;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Snapshot of Bible chapter reading progress for syncing.
/// Keys are book codes (e.g. "gen"); values are arrays of read chapter numbers.
/// </summary>
public sealed class BibleReadingProgressSnapshot : SyncEntity
{
    public Dictionary<string, int[]> ReadChapters { get; set; } = [];
}
