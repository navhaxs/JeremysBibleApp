using System;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Base class for entities that can be synced to Google Drive
/// </summary>
public abstract class SyncEntity
{
    /// <summary>
    /// Unique identifier for this sync entity
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// UTC timestamp of last modification
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Unique identifier of the device that made the last modification
    /// </summary>
    public string? LastModifiedByDeviceId { get; set; }

    /// <summary>
    /// Version number for conflict resolution
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Sync status of this entity
    /// </summary>
    public SyncStatus SyncStatus { get; set; } = SyncStatus.Pending;
}

/// <summary>
/// Represents the sync status of an entity
/// </summary>
public enum SyncStatus
{
    /// <summary>
    /// Not yet synced
    /// </summary>
    Pending,

    /// <summary>
    /// Successfully synced
    /// </summary>
    Synced,

    /// <summary>
    /// Sync failed, pending retry
    /// </summary>
    Failed,

    /// <summary>
    /// Marked for deletion
    /// </summary>
    Deleted
}


