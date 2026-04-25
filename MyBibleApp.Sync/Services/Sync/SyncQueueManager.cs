using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Represents a queued sync operation
/// </summary>
public sealed class SyncQueueItem
{
    /// <summary>
    /// Unique identifier for this queue item
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Type of operation (ReadingProgress, Annotation, Preferences)
    /// </summary>
    public string OperationType { get; set; } = string.Empty;

    /// <summary>
    /// Inline JSON data for this operation
    /// </summary>
    public System.Text.Json.JsonElement Data { get; set; }

    /// <summary>
    /// Timestamp when the operation was queued
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of times this operation has been retried
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Whether this operation has been synced
    /// </summary>
    public bool IsSynced { get; set; }
}

/// <summary>
/// Manages queuing of sync operations for offline support
/// </summary>
public interface ISyncQueueManager
{
    /// <summary>
    /// Adds an operation to the sync queue
    /// </summary>
    Task QueueOperationAsync(string operationType, object data);

    /// <summary>
    /// Gets all pending operations in the queue
    /// </summary>
    Task<List<SyncQueueItem>> GetPendingOperationsAsync();

    /// <summary>
    /// Marks an operation as synced
    /// </summary>
    Task MarkAsSyncedAsync(string queueItemId);

    /// <summary>
    /// Removes an operation from the queue
    /// </summary>
    Task RemoveOperationAsync(string queueItemId);

    /// <summary>
    /// Clears all operations from the queue
    /// </summary>
    Task ClearQueueAsync();

    /// <summary>
    /// Gets the number of pending operations
    /// </summary>
    Task<int> GetPendingCountAsync();
}

/// <summary>
/// File-based implementation of sync queue manager
/// </summary>
public class FileSyncQueueManager : ISyncQueueManager
{
    private const string QueueFileName = "sync_queue.json";
    private readonly string _queueFilePath;

    public FileSyncQueueManager(string? storagePath = null)
    {
        storagePath ??= SyncStoragePaths.GetQueueStorageDirectory();
        _queueFilePath = Path.Combine(storagePath, QueueFileName);
        if (!Directory.Exists(storagePath))
            Directory.CreateDirectory(storagePath);
    }

    public async Task QueueOperationAsync(string operationType, object data)
    {
        FileAccessCoordinator.Execute(_queueFilePath, () =>
        {
            try
            {
                var queue = LoadQueueUnsafe();

                var item = new SyncQueueItem
                {
                    OperationType = operationType,
                    Data = JsonSerializer.SerializeToElement(data),
                    QueuedAt = DateTime.UtcNow
                };

                queue.Add(item);
                if (CompactPendingOperations(queue))
                {
                    System.Diagnostics.Debug.WriteLine("Compacted redundant pending sync operations before saving queue.");
                }

                SaveQueueUnsafe(queue);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error queuing operation: {ex.Message}");
            }
        });

        await Task.CompletedTask;
    }

    public async Task<List<SyncQueueItem>> GetPendingOperationsAsync()
    {
        return await Task.Run(() =>
        {
            return FileAccessCoordinator.Execute(_queueFilePath, () =>
            {
                try
                {
                    var queue = LoadQueueUnsafe();
                    if (CompactPendingOperations(queue))
                        SaveQueueUnsafe(queue);

                    return queue.Where(x => !x.IsSynced).ToList();
                }
                catch
                {
                    return [];
                }
            });
        });
    }

    public async Task MarkAsSyncedAsync(string queueItemId)
    {
        // Remove the item outright — once synced it has no further purpose.
        // Keeping it as IsSynced=true would cause the file to grow unboundedly.
        await RemoveOperationAsync(queueItemId).ConfigureAwait(false);
    }

    public async Task RemoveOperationAsync(string queueItemId)
    {
        FileAccessCoordinator.Execute(_queueFilePath, () =>
        {
            try
            {
                var queue = LoadQueueUnsafe();
                queue.RemoveAll(x => x.Id == queueItemId);
                SaveQueueUnsafe(queue);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error removing operation: {ex.Message}");
            }
        });

        await Task.CompletedTask;
    }

    public async Task ClearQueueAsync()
    {
        FileAccessCoordinator.Execute(_queueFilePath, () =>
        {
            try
            {
                if (File.Exists(_queueFilePath))
                    File.Delete(_queueFilePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error clearing queue: {ex.Message}");
            }
        });

        await Task.CompletedTask;
    }

    public async Task<int> GetPendingCountAsync()
    {
        return await Task.Run(() =>
        {
            return FileAccessCoordinator.Execute(_queueFilePath, () =>
            {
                try
                {
                    var queue = LoadQueueUnsafe();
                    if (CompactPendingOperations(queue))
                        SaveQueueUnsafe(queue);

                    return queue.Count(x => !x.IsSynced);
                }
                catch
                {
                    return 0;
                }
            });
        });
    }

    private List<SyncQueueItem> LoadQueueUnsafe()
    {
        if (!File.Exists(_queueFilePath))
            return [];

        try
        {
            var json = File.ReadAllText(_queueFilePath);
            var queue = JsonSerializer.Deserialize<List<SyncQueueItem>>(json);
            return queue ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveQueueUnsafe(List<SyncQueueItem> queue)
    {
        try
        {
            var json = JsonSerializer.Serialize(queue, new JsonSerializerOptions { WriteIndented = true });
            FileAccessCoordinator.WriteAllTextAtomically(_queueFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving queue: {ex.Message}");
        }
    }

    private static bool CompactPendingOperations(List<SyncQueueItem> queue)
    {
        var changed = false;
        // Keep only the latest UserData item (covers both progress + preferences).
        // Also compact legacy single-field types that may remain from an older build.
        changed |= CompactLatestPendingOperation(queue, "UserData");
        changed |= CompactLatestPendingOperation(queue, "Preferences");
        changed |= CompactLatestPendingOperation(queue, "ReadingProgress");
        return changed;
    }

    private static bool CompactLatestPendingOperation(List<SyncQueueItem> queue, string operationType)
    {
        var pendingItems = queue
            .Where(item => !item.IsSynced && string.Equals(item.OperationType, operationType, StringComparison.Ordinal))
            .OrderBy(item => item.QueuedAt)
            .ToList();

        if (pendingItems.Count <= 1)
            return false;

        var latestItemId = pendingItems[^1].Id;
        var removedCount = queue.RemoveAll(item =>
            !item.IsSynced
            && string.Equals(item.OperationType, operationType, StringComparison.Ordinal)
            && !string.Equals(item.Id, latestItemId, StringComparison.Ordinal));

        return removedCount > 0;
    }
}

