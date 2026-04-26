using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Represents the result of a sync operation
/// </summary>
public sealed class SyncResult
{
    /// <summary>
    /// Whether the sync operation was successful
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Number of items synced
    /// </summary>
    public int ItemsSynced { get; set; }

    /// <summary>
    /// Number of conflicts encountered
    /// </summary>
    public int ConflictsResolved { get; set; }

    /// <summary>
    /// Error message if unsuccessful
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Detailed log of what was synced
    /// </summary>
    public List<string> SyncLog { get; set; } = [];

    public static SyncResult Success(int itemsSynced, int conflictsResolved = 0)
        => new() { IsSuccess = true, ItemsSynced = itemsSynced, ConflictsResolved = conflictsResolved };

    public static SyncResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Represents sync status and statistics
/// </summary>
public sealed class SyncStatusInfo
{
    /// <summary>
    /// Whether sync is currently in progress
    /// </summary>
    public bool IsSyncing { get; set; }

    /// <summary>
    /// Last successful sync time (UTC)
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Number of pending items in the sync queue
    /// </summary>
    public int PendingItemsCount { get; set; }

    /// <summary>
    /// Progress percentage (0-100)
    /// </summary>
    public int ProgressPercentage { get; set; }

    /// <summary>
    /// Current sync status message
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;
}

/// <summary>
/// Delegate for sync progress updates
/// </summary>
public delegate void SyncProgressHandler(SyncStatusInfo status);

/// <summary>
/// Result of a pull operation from Google Drive
/// </summary>
public sealed class PullResult
{
    public bool IsSuccess { get; set; }
    public bool HadChanges { get; set; }
    public ReadingProgressSnapshot? ReadingProgress { get; set; }
    public PreferencesSnapshot? Preferences { get; set; }
    public BibleReadingProgressSnapshot? BibleReadingProgress { get; set; }
    public string? ErrorMessage { get; set; }

    public static PullResult Success(bool hadChanges,
        ReadingProgressSnapshot? rp = null,
        PreferencesSnapshot? prefs = null,
        BibleReadingProgressSnapshot? bibleReading = null)
        => new() { IsSuccess = true, HadChanges = hadChanges, ReadingProgress = rp, Preferences = prefs, BibleReadingProgress = bibleReading };

    public static PullResult NoChanges()
        => new() { IsSuccess = true, HadChanges = false };

    public static PullResult Failure(string error)
        => new() { IsSuccess = false, ErrorMessage = error };
}

/// <summary>
/// Combined user data written to a single Drive file, reducing API round-trips.
/// Both fields are nullable — deserialisation of a file that predates one field
/// will simply leave that field null.
/// </summary>
public sealed class UserDataSnapshot
{
    public ReadingProgressSnapshot? ReadingProgress { get; set; }
    public PreferencesSnapshot? Preferences { get; set; }
    public BibleReadingProgressSnapshot? BibleReadingProgress { get; set; }
}

/// <summary>
/// Interface for Google Drive sync operations
/// </summary>
public interface IGoogleDriveSyncService
{
    /// <summary>
    /// Current sync status
    /// </summary>
    SyncStatusInfo CurrentStatus { get; }

    /// <summary>
    /// Event raised when sync progress changes
    /// </summary>
    event SyncProgressHandler? SyncProgress;

    /// <summary>
    /// Initiates a full sync of all data
    /// </summary>
    Task<SyncResult> SyncAllAsync();

    /// <summary>
    /// Writes the combined user-data snapshot (reading progress + preferences)
    /// to a single Drive file.
    /// </summary>
    Task<SyncResult> SaveUserDataAsync(UserDataSnapshot data);

    /// <summary>
    /// Reads the combined user-data snapshot from Drive.
    /// Returns null if the file does not yet exist.
    /// </summary>
    Task<UserDataSnapshot?> GetUserDataAsync();

    /// <summary>
    /// Syncs annotations for a specific verse
    /// </summary>
    Task<SyncResult> SyncAnnotationAsync(AnnotationBundle annotation);

    /// <summary>
    /// Gets all annotations from Drive
    /// </summary>
    Task<List<AnnotationBundle>> GetAllAnnotationsAsync();

    /// <summary>
    /// Clears all sync data from Drive (caution!)
    /// </summary>
    Task<bool> ClearRemoteSyncDataAsync();

    /// <summary>
    /// Returns the Drive modifiedTime for each known sync file (single metadata-only API call).
    /// Key: "user_data.json".
    /// </summary>
    Task<Dictionary<string, DateTime?>> GetFileModifiedTimesAsync();
}

