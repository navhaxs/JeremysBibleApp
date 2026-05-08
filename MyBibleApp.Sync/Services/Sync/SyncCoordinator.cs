using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Event args for sync progress updates
/// </summary>
public class SyncProgressEventArgs : EventArgs
{
    public bool IsSyncing { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Progress { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsError { get; set; }
}

/// <summary>
/// Orchestrates sync operations and manages offline queue
/// </summary>
public interface ISyncCoordinator : IDisposable
{
    /// <summary>
    /// Attempts authentication using cached credentials only — never opens a browser.
    /// Safe to call on startup without risking a hang.
    /// </summary>
    Task<bool> TrySilentAuthAsync();

    /// <summary>
    /// Authenticates with Google Drive (may open a browser / interactive prompt).
    /// </summary>
    Task<bool> AuthenticateAsync(string? code = null);

    /// <summary>
    /// Synchronizes reading progress
    /// </summary>
    Task<SyncResult> SyncReadingProgressAsync(string bookCode, int chapter, int verse);

    /// <summary>
    /// Synchronizes an annotation
    /// </summary>
    Task<SyncResult> SyncAnnotationAsync(AnnotationBundle annotation);

    /// <summary>
    /// Synchronizes preferences
    /// </summary>
    Task<SyncResult> SyncPreferencesAsync(PreferencesSnapshot preferences);

    /// <summary>
    /// Synchronizes Bible chapter reading progress (which chapters have been read)
    /// </summary>
    Task<SyncResult> SyncBibleReadingProgressAsync(Dictionary<string, int[]> readChapters);

    /// <summary>
    /// Starts automatic background sync
    /// </summary>
    void StartAutoSync(TimeSpan interval);

    /// <summary>
    /// Stops automatic background sync
    /// </summary>
    void StopAutoSync();

    /// <summary>
    /// Forces a sync operation now
    /// </summary>
    void ForceSync();

    /// <summary>
    /// Forces a sync operation and awaits its completion
    /// </summary>
    Task ForceSyncAsync();

    /// <summary>
    /// Pulls data from Google Drive, applies last-write-wins conflict resolution against local
    /// storage, then pushes any pending local changes. Uses Drive file modifiedTime as a cache
    /// key to skip downloads when nothing has changed on the remote side.
    /// Returns the data that was newer on the remote (if any) for the caller to apply to UI.
    /// </summary>
    Task<PullResult> PullFromDriveAsync();

    /// <summary>
    /// Signs out the current user
    /// </summary>
    void SignOut();

    /// <summary>
    /// Cancels any in-progress interactive authentication flow.
    /// </summary>
    void CancelAuthentication();

    /// <summary>
    /// Re-opens the browser to the pending OAuth URL when an interactive auth is in progress.
    /// </summary>
    void ReopenBrowser();

    /// <summary>
    /// Event for sync progress updates
    /// </summary>
    event EventHandler<SyncProgressEventArgs>? SyncProgress;
}

/// <summary>
/// Default implementation of sync coordinator
/// </summary>
public class SyncCoordinator : ISyncCoordinator
{
    private readonly IGoogleDriveAuthService _authService;
    private readonly IGoogleDriveSyncService _syncService;
    private readonly ISyncQueueManager _queueManager;
    private readonly INetworkStatusMonitor _networkMonitor;
    private readonly ILocalStorageProvider _localStorage;

    private CancellationTokenSource? _autoSyncCts;
    private bool _isOffline;
    private bool _disposed;

    public event EventHandler<SyncProgressEventArgs>? SyncProgress;

    public SyncCoordinator(
        IGoogleDriveAuthService authService,
        IGoogleDriveSyncService syncService,
        ISyncQueueManager queueManager,
        INetworkStatusMonitor networkMonitor,
        ILocalStorageProvider localStorage)
    {
        _authService = authService;
        _syncService = syncService;
        _queueManager = queueManager;
        _networkMonitor = networkMonitor;
        _localStorage = localStorage;

        _networkMonitor.ConnectivityChanged += OnNetworkConnectivityChanged;
        _syncService.SyncProgress += OnSyncServiceProgress;
        _isOffline = !_networkMonitor.IsConnected;

        _networkMonitor.StartMonitoring();
    }

    public async Task<bool> TrySilentAuthAsync()
    {
        var result = await _authService.TrySilentAuthAsync().ConfigureAwait(false);
        if (result.IsSuccess)
            await _localStorage.SaveAsync("LastAuthenticatedUser", result.UserEmail ?? "Unknown").ConfigureAwait(false);
        return result.IsSuccess;
    }

    public async Task<bool> AuthenticateAsync(string? code = null)
    {
        var result = await _authService.AuthenticateAsync().ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await _localStorage.SaveAsync("LastAuthenticatedUser", result.UserEmail ?? "Unknown").ConfigureAwait(false);
            return true;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Authentication failed."
                : result.ErrorMessage);
    }

    public async Task<SyncResult> SyncReadingProgressAsync(string bookCode, int chapter, int verse)
    {
        var progress = new ReadingProgressSnapshot
        {
            BookCode = bookCode,
            Chapter = chapter,
            Verse = verse
        };

        // Always persist locally so progress survives an immediate restart.
        await _localStorage.SaveObjectAsync("CurrentReadingProgress", progress).ConfigureAwait(false);

        // Reading progress is local-only — not synced to the cloud.
        return SyncResult.Success(1);
    }

    public async Task<SyncResult> SyncAnnotationAsync(AnnotationBundle annotation)
    {
        // Queue for the next scheduled sync
        await _queueManager.QueueOperationAsync("Annotation", annotation).ConfigureAwait(false);
        return SyncResult.Success(1);
    }

    public async Task<SyncResult> SyncPreferencesAsync(PreferencesSnapshot preferences)
    {
        // Save locally only — preferences are not synced to the cloud.
        await _localStorage.SaveObjectAsync("UserPreferences", preferences).ConfigureAwait(false);
        return SyncResult.Success(1);
    }

    public async Task<SyncResult> SyncBibleReadingProgressAsync(Dictionary<string, int[]> readChapters)
    {
        var snapshot = new BibleReadingProgressSnapshot { ReadChapters = readChapters };
        await _localStorage.SaveObjectAsync("BibleReadingProgress", snapshot).ConfigureAwait(false);

        await _queueManager.QueueOperationAsync("BibleReadingProgress", snapshot).ConfigureAwait(false);
        return SyncResult.Success(1);
    }

    public void StartAutoSync(TimeSpan interval)
    {
        StopAutoSync();

        _autoSyncCts = new CancellationTokenSource();
        _ = AutoSyncLoop(interval, _autoSyncCts.Token);
    }

    public void StopAutoSync()
    {
        _autoSyncCts?.Cancel();
        _autoSyncCts?.Dispose();
        _autoSyncCts = null;
    }

    public void ForceSync()
    {
        _ = ForceSyncAsync();
    }

    public async Task ForceSyncAsync()
    {
        if (_isOffline || !_authService.IsAuthenticated)
        {
            RaiseSyncProgress(false, "Cannot sync: not authenticated or offline", 0);
            return;
        }

        await PullFromDriveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Drains the pending queue without emitting its own progress events.
    /// Used by PullFromDriveAsync so the caller controls progress messaging.
    /// </summary>
    private async Task<int> DrainQueueAsync(List<SyncQueueItem>? pendingOps = null, bool reportProgress = false)
    {
        pendingOps ??= await _queueManager.GetPendingOperationsAsync().ConfigureAwait(false);

        var processedCount = 0;
        foreach (var op in pendingOps)
        {
            try
            {
                if (reportProgress)
                    RaiseSyncProgress(
                        true,
                        $"Saving {processedCount + 1} of {pendingOps.Count}: {DescribeOperation(op.OperationType)}...",
                        CalculateProgress(processedCount, pendingOps.Count));

                var result = await ProcessQueuedOperationAsync(op).ConfigureAwait(false);
                if (result.IsSuccess)
                    await _queueManager.MarkAsSyncedAsync(op.Id).ConfigureAwait(false);

                processedCount++;

                if (reportProgress)
                    RaiseSyncProgress(
                        true,
                        $"Saved {processedCount} of {pendingOps.Count}: {DescribeOperation(op.OperationType)}.",
                        CalculateProgress(processedCount, pendingOps.Count));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing queued operation: {ex.Message}");
                if (reportProgress)
                    RaiseSyncProgress(false, $"Sync error: {ex.Message}", CalculateProgress(processedCount, pendingOps.Count));
            }
        }

        return processedCount;
    }

    public void ForceSyncAllAsync()
    {
        _ = Task.Run(async () =>
        {
            if (_isOffline || !_authService.IsAuthenticated)
            {
                RaiseSyncProgress(false, "Cannot sync: not authenticated or offline", 0);
                return;
            }

            RaiseSyncProgress(true, "Starting full sync...", 10);

            try
            {
                // Sync all pending queue items first
                var pendingOps = await _queueManager.GetPendingOperationsAsync().ConfigureAwait(false);
                foreach (var op in pendingOps)
                {
                    try
                    {
                        var result = await ProcessQueuedOperationAsync(op).ConfigureAwait(false);
                        if (result.IsSuccess)
                        {
                            await _queueManager.MarkAsSyncedAsync(op.Id).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing queued operation: {ex.Message}");
                    }
                }

                RaiseSyncProgress(true, "Sync completed", 100);
                RaiseSyncProgress(false, "Sync completed", 100);
            }
            catch (Exception ex)
            {
                RaiseSyncProgress(false, $"Sync error: {ex.Message}", 0);
            }
        });
    }

    public void SignOut()
    {
        StopAutoSync();
        _ = _authService.RevokeAsync();
    }

    public void CancelAuthentication() => _authService.CancelAuthentication();

    public void ReopenBrowser() => _authService.ReopenBrowser();

    public async Task<PullResult> PullFromDriveAsync()
    {
        if (_isOffline || !_authService.IsAuthenticated)
            return PullResult.Failure("Not authenticated or offline");

        try
        {
            RaiseSyncProgress(true, "Checking Google Drive for updates...", 10);

            // One metadata-only API call to get modifiedTime for user_data.json
            var remoteTimes = await _syncService.GetFileModifiedTimesAsync().ConfigureAwait(false);

            BibleReadingProgressSnapshot? pulledBibleReading = null;

            var remoteTime = remoteTimes.GetValueOrDefault("user_data.json");
            var cachedTime = await GetCachedModifiedTimeAsync("user_data.json").ConfigureAwait(false);

            if (remoteTime.HasValue && remoteTime != cachedTime)
            {
                RaiseSyncProgress(true, "Downloading data from Drive...", 40);

                var remote = await _syncService.GetUserDataAsync().ConfigureAwait(false);

                if (remote?.BibleReadingProgress is { } remoteBibleReading)
                {
                    var local = await _localStorage
                        .GetObjectAsync<BibleReadingProgressSnapshot>("BibleReadingProgress")
                        .ConfigureAwait(false);

                    if (local == null || remoteBibleReading.LastModified > local.LastModified)
                    {
                        await _localStorage.SaveObjectAsync("BibleReadingProgress", remoteBibleReading).ConfigureAwait(false);
                        pulledBibleReading = remoteBibleReading;
                    }
                }

                await SaveCachedModifiedTimeAsync("user_data.json", remoteTime.Value).ConfigureAwait(false);
            }

            // ── Push any locally-pending changes (silently — PullFromDriveAsync owns progress) ──
            var pendingOps = await _queueManager.GetPendingOperationsAsync().ConfigureAwait(false);
            if (pendingOps.Count > 0)
            {
                RaiseSyncProgress(true, "Uploading local changes to Drive...", 80);
                await DrainQueueAsync(pendingOps).ConfigureAwait(false);
            }

            var hadChanges = pulledBibleReading != null;
            RaiseSyncProgress(false, hadChanges ? "Sync complete — remote changes applied." : "Sync complete — already up to date.", 100);

            return PullResult.Success(hadChanges, bibleReading: pulledBibleReading);
        }
        catch (Exception ex)
        {
            RaiseSyncProgress(false, $"Sync error: {ex.Message}", 0);
            return PullResult.Failure(ex.Message);
        }
    }

    private static readonly string ModTimeKeyPrefix = "DriveModTime_";

    private async Task<DateTime?> GetCachedModifiedTimeAsync(string fileName)
    {
        var stored = await _localStorage.GetAsync(ModTimeKeyPrefix + fileName).ConfigureAwait(false);
        return stored != null && DateTime.TryParse(stored, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;
    }

    private async Task SaveCachedModifiedTimeAsync(string fileName, DateTime modifiedTime)
    {
        await _localStorage.SaveAsync(ModTimeKeyPrefix + fileName, modifiedTime.ToString("O")).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopAutoSync();
        _networkMonitor.StopMonitoring();
        _networkMonitor.ConnectivityChanged -= OnNetworkConnectivityChanged;
        _syncService.SyncProgress -= OnSyncServiceProgress;
    }

    private async Task AutoSyncLoop(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                if (_isOffline || !_authService.IsAuthenticated)
                    continue;

                // Sync all pending queue items
                var pendingOps = await _queueManager.GetPendingOperationsAsync().ConfigureAwait(false);
                foreach (var op in pendingOps)
                {
                    try
                    {
                        var result = await ProcessQueuedOperationAsync(op).ConfigureAwait(false);
                        if (result.IsSuccess)
                        {
                            await _queueManager.MarkAsSyncedAsync(op.Id).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing queued operation: {ex.Message}");
                    }
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in auto-sync loop: {ex.Message}");
            }
        }
    }

    private async Task<SyncResult> ProcessQueuedOperationAsync(SyncQueueItem item)
    {
        return item.OperationType switch
        {
            "BibleReadingProgress" => await _syncService.SaveUserDataAsync(new UserDataSnapshot
            {
                BibleReadingProgress = item.Data.Deserialize<BibleReadingProgressSnapshot>(JsonHelper.Options)
            }).ConfigureAwait(false),

            "Annotation" => await _syncService.SyncAnnotationAsync(
                item.Data.Deserialize<AnnotationBundle>(JsonHelper.Options) ?? new AnnotationBundle()
            ).ConfigureAwait(false),

            // Legacy queue items — no longer synced to the cloud; discard silently.
            "UserData" or "ReadingProgress" or "Preferences" => SyncResult.Success(0),

            _ => SyncResult.Failure($"Unknown operation type: {item.OperationType}")
        };
    }

    private void OnNetworkConnectivityChanged(bool isConnected)
    {
        _isOffline = !isConnected;

        if (isConnected && _authService.IsAuthenticated)
        {
            // Trigger sync when connectivity is restored
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // Brief delay for network stabilization
                var pendingOps = await _queueManager.GetPendingOperationsAsync().ConfigureAwait(false);
                if (pendingOps.Count > 0)
                {
                    RaiseSyncProgress(true, "Syncing queued operations...", 0);
                    await _syncService.SyncAllAsync().ConfigureAwait(false);
                }
            });
        }
    }

    private void OnSyncServiceProgress(SyncStatusInfo status)
    {
        RaiseSyncProgress(status.IsSyncing, status.StatusMessage, status.ProgressPercentage);
    }

    private void RaiseSyncProgress(bool isSyncing, string message, int progress)
    {
        SyncProgress?.Invoke(this, new SyncProgressEventArgs
        {
            IsSyncing = isSyncing,
            Message = message,
            Progress = progress,
            IsCompleted = !isSyncing && progress == 100,
            IsError = message.Contains("error", StringComparison.OrdinalIgnoreCase)
        });
    }

    private static int CalculateProgress(int completedCount, int totalCount)
    {
        if (totalCount <= 0)
            return 100;

        var rawProgress = (int)Math.Round((double)completedCount / totalCount * 100, MidpointRounding.AwayFromZero);
        return Math.Clamp(rawProgress, 0, 100);
    }

    private static string DescribeOperation(string operationType)
    {
        return operationType switch
        {
            "ReadingProgress" => "reading progress",
            "Annotation" => "annotation",
            "Preferences" => "preferences",
            _ => operationType
        };
    }
}

/// <summary>
/// Helper for JSON serialization
/// </summary>
internal static class JsonHelper
{
    internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string Serialize<T>(T obj) => System.Text.Json.JsonSerializer.Serialize(obj, Options);

    public static T? Deserialize<T>(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(json, Options);
}



