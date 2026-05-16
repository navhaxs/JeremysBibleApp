using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using MyBibleApp.Models;
using MyBibleApp.Services;
using MyBibleApp.Services.Sync;
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public class AppViewModel : ViewModelBase, IDisposable
{
    private const string LocalTabStateKey = "LocalTabState";
    private const string DebugModeKey = "IsDebugMode";
    private const int DebugOverlayMaxLines = 12;

    private readonly ISyncCoordinator? _syncCoordinator;
    private readonly ISyncQueueManager? _syncQueueManager;
    private readonly IGoogleDriveSyncService? _googleDriveSyncService;
    private readonly IGoogleDriveAuthService? _googleDriveAuthService;
    private readonly ILocalStorageProvider? _localStorageProvider;

    private bool _isDebugMode;
    private bool _isSyncing;
    private bool _isAuthenticated;
    private bool _isAuthenticating;
    private string? _currentUserEmail;
    private string _syncStatus = string.Empty;
    private string _syncDebugData = string.Empty;
    private string _syncDebugLastUpdated = "Never";
    private IReadOnlyList<string> _syncDebugLogs = [];
    private IReadOnlyList<string> _debugLogOverlayLines = [];
    private readonly ObservableCollection<string> _debugLogOverlayItems = [];

    public AppViewModel()
    {
        try
        {
            var sharedSyncRuntime = SharedSyncRuntime.Instance;

            _googleDriveAuthService = sharedSyncRuntime.GoogleDriveAuthService;
            _googleDriveSyncService = sharedSyncRuntime.GoogleDriveSyncService;
            _syncQueueManager = sharedSyncRuntime.SyncQueueManager;
            _localStorageProvider = sharedSyncRuntime.LocalStorageProvider;
            _syncCoordinator = sharedSyncRuntime.SyncCoordinator;

            _syncCoordinator.SyncProgress += OnSyncProgress;
            _googleDriveAuthService.AuthStateChanged += OnAuthStateChanged;
            IsAuthenticated = _googleDriveAuthService.IsAuthenticated;
            CurrentUserEmail = _googleDriveAuthService.CurrentUserEmail;

            SyncStatus = "Sync initialized";
            AppendSyncDebugLog("Sync services initialized.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize sync: {ex.Message}");
            AppendSyncDebugLog($"Failed to initialize sync: {ex.Message}");
        }
    }

    // ── Debug Mode ───────────────────────────────────────────────────────────

    public bool IsDebugMode
    {
        get => _isDebugMode;
        set
        {
            var old = _isDebugMode;
            this.RaiseAndSetIfChanged(ref _isDebugMode, value);
            if (old != _isDebugMode)
                _ = PersistDebugModeAsync(_isDebugMode);
        }
    }

    private async Task PersistDebugModeAsync(bool value)
    {
        if (_localStorageProvider == null) return;
        try
        {
            await _localStorageProvider.SaveAsync(DebugModeKey, value ? "true" : "false").ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    public async Task LoadDebugModeFromStorageAsync()
    {
        if (_localStorageProvider == null) return;
        try
        {
            var stored = await _localStorageProvider.GetAsync(DebugModeKey).ConfigureAwait(false);
            if (string.Equals(stored, "true", StringComparison.OrdinalIgnoreCase))
                await Dispatcher.UIThread.InvokeAsync(() => _isDebugMode = true);
            this.RaisePropertyChanged(nameof(IsDebugMode));
        }
        catch { /* best-effort */ }
    }

    // ── Sync Status ──────────────────────────────────────────────────────────

    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSyncing, value);
            this.RaisePropertyChanged(nameof(CanForceSync));
        }
    }

    public string SyncStatus
    {
        get => _syncStatus;
        private set => this.RaiseAndSetIfChanged(ref _syncStatus, value);
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        set
        {
            this.RaiseAndSetIfChanged(ref _isAuthenticated, value);
            this.RaisePropertyChanged(nameof(CanAuthenticate));
            this.RaisePropertyChanged(nameof(CanForceSync));
        }
    }

    public bool CanAuthenticate => !IsAuthenticated;

    public bool CanForceSync => IsAuthenticated && !IsSyncing;

    public bool IsAuthenticating
    {
        get => _isAuthenticating;
        private set => this.RaiseAndSetIfChanged(ref _isAuthenticating, value);
    }

    public string? CurrentUserEmail
    {
        get => _currentUserEmail;
        private set => this.RaiseAndSetIfChanged(ref _currentUserEmail, value);
    }

    // ── Debug Logs ───────────────────────────────────────────────────────────

    public IReadOnlyList<string> SyncDebugLogs
    {
        get => _syncDebugLogs;
        private set => this.RaiseAndSetIfChanged(ref _syncDebugLogs, value);
    }

    public ObservableCollection<string> DebugLogOverlayItems => _debugLogOverlayItems;

    public IReadOnlyList<string> DebugLogOverlayLines
    {
        get => _debugLogOverlayLines;
        private set => this.RaiseAndSetIfChanged(ref _debugLogOverlayLines, value);
    }

    public string SyncDebugData
    {
        get => _syncDebugData;
        private set => this.RaiseAndSetIfChanged(ref _syncDebugData, value);
    }

    public string SyncDebugLastUpdated
    {
        get => _syncDebugLastUpdated;
        private set => this.RaiseAndSetIfChanged(ref _syncDebugLastUpdated, value);
    }

    // ── Reading Progress Sync Suppression ────────────────────────────────────

    public bool SuppressReadingProgressSync { get; set; }

    // ── Auth Methods ─────────────────────────────────────────────────────────

    public async Task<bool> AuthenticateAsync(string? code = null)
    {
        if (_syncCoordinator == null)
        {
            AppendSyncDebugLog("Authentication unavailable: sync services failed to initialize.");
            return false;
        }

        IsAuthenticating = true;
        try
        {
            AppendSyncDebugLog($"[Auth] Starting authentication. Platform={PlatformHelper.GetPlatformName()}, IsAuthenticated={IsAuthenticated}, CanAuthenticate={CanAuthenticate}.");
            if (PlatformHelper.IsAndroid)
                AppendSyncDebugLog($"[Auth] Android: LaunchUri set={AndroidOAuthCallbackBridge.LaunchUri != null}.");
            AppendSyncDebugLog("Starting authentication... a browser window should open.");
            var success = await _syncCoordinator.AuthenticateAsync(code);
            IsAuthenticated = success;
            AppendSyncDebugLog(success
                ? $"Authentication succeeded ({_googleDriveAuthService?.CurrentUserEmail ?? "user"})."
                : "Authentication did not complete.");
            await RefreshSyncDebugDataAsync();
            return success;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Authentication error: {ex.Message}");
            AppendSyncDebugLog($"Authentication error: {ex.Message}");
            IsAuthenticated = false;
            return false;
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    public void CancelAuthentication()
    {
        _syncCoordinator?.CancelAuthentication();
    }

    public void ReopenAuthBrowser()
    {
        _syncCoordinator?.ReopenBrowser();
    }

    public async Task<bool> HasPreviousAuthenticationAsync()
    {
        if (_localStorageProvider == null) return false;
        try
        {
            var lastUser = await _localStorageProvider.GetAsync("LastAuthenticatedUser").ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(lastUser);
        }
        catch
        {
            return false;
        }
    }

    public async Task SyncAuthStateWithServiceAsync()
    {
        if (_googleDriveAuthService == null)
            return;

        var actuallyAuthenticated = _googleDriveAuthService.IsAuthenticated;
        if (IsAuthenticated != actuallyAuthenticated)
        {
            IsAuthenticated = actuallyAuthenticated;
            AppendSyncDebugLog($"Auth state synced: {(actuallyAuthenticated ? "Authenticated" : "Not authenticated")}");
        }
    }

    public async Task<bool> TryAutoAuthenticateOnStartupAsync()
    {
        if (_syncCoordinator == null || IsAuthenticated)
            return IsAuthenticated;

        try
        {
            var authenticated = await _syncCoordinator.TrySilentAuthAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => IsAuthenticated = authenticated);

            AppendSyncDebugLog(authenticated
                ? "Auto-authentication succeeded from cached credentials."
                : "Auto-authentication was not available.");

            await RefreshSyncDebugDataAsync().ConfigureAwait(false);
            return authenticated;
        }
        catch (Exception ex)
        {
            AppendSyncDebugLog($"Auto-authentication error: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => IsAuthenticated = false);
            return false;
        }
    }

    public void SignOut()
    {
        if (_syncCoordinator != null && IsAuthenticated)
        {
            _syncCoordinator.SignOut();
            IsAuthenticated = false;
            AppendSyncDebugLog("Signed out from sync account.");
        }
    }

    // ── Sync Methods ─────────────────────────────────────────────────────────

    public void ForceSync()
    {
        _syncCoordinator?.ForceSync();
        AppendSyncDebugLog("Manual sync requested.");
        _ = RefreshSyncDebugDataAsync();
    }

    public async Task ForceSyncAsync()
    {
        if (_syncCoordinator == null || !IsAuthenticated)
            return;

        AppendSyncDebugLog("Sync on shutdown requested.");
        await _syncCoordinator.ForceSyncAsync().ConfigureAwait(false);
    }

    public async Task<PullResult> PullFromDriveAsync()
    {
        if (_syncCoordinator == null || !IsAuthenticated)
            return PullResult.NoChanges();

        AppendSyncDebugLog("Pull from Drive requested.");
        return await _syncCoordinator.PullFromDriveAsync().ConfigureAwait(false);
    }

    public async Task SyncReadingProgressAsync(string bookCode, int chapter, int verse)
    {
        if (_syncCoordinator == null)
            return;

        await SyncAuthStateWithServiceAsync().ConfigureAwait(false);

        if (!IsAuthenticated)
            return;

        if (string.IsNullOrWhiteSpace(bookCode))
            return;

        var result = await _syncCoordinator.SyncReadingProgressAsync(
            bookCode,
            Math.Max(1, chapter),
            Math.Max(1, verse)
        ).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            AppendSyncDebugLog($"Failed to sync reading progress: {result.ErrorMessage}");
        }
    }

    // ── Tab Persistence ──────────────────────────────────────────────────────

    public async Task PersistOpenTabReferencesAsync(IReadOnlyList<OpenTabReferenceState> openTabs, int activeTabIndex)
    {
        try
        {
            if (_localStorageProvider == null)
                return;

            var state = new LocalTabStateData { Tabs = openTabs.ToList(), ActiveTabIndex = activeTabIndex };
            await _localStorageProvider.SaveObjectAsync(LocalTabStateKey, state).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppendSyncDebugLog($"PersistOpenTabReferencesAsync error: {ex.Message}");
        }
    }

    public async Task<(IReadOnlyList<OpenTabReferenceState> OpenTabs, int ActiveTabIndex)> LoadPersistedOpenTabReferencesAsync()
    {
        try
        {
            if (_localStorageProvider == null)
                return ([], 0);

            var state = await _localStorageProvider.GetObjectAsync<LocalTabStateData>(LocalTabStateKey).ConfigureAwait(false);

            if (state?.Tabs == null || state.Tabs.Count == 0)
                return ([], 0);

            var tabs = state.Tabs
                .Where(t => !string.IsNullOrWhiteSpace(t.BookCode) || !string.IsNullOrWhiteSpace(t.Header))
                .OrderBy(t => t.TabIndex)
                .ToList();

            if (tabs.Count == 0)
                return ([], 0);

            var activeIndex = Math.Clamp(state.ActiveTabIndex, 0, tabs.Count - 1);
            return (tabs, activeIndex);
        }
        catch (Exception ex)
        {
            AppendSyncDebugLog($"LoadPersistedOpenTabReferencesAsync error: {ex.Message}");
            return ([], 0);
        }
    }

    // ── Debug Data ───────────────────────────────────────────────────────────

    public async Task RefreshSyncDebugDataAsync()
    {
        var lines = new List<string>
        {
            $"Timestamp (UTC): {DateTime.UtcNow:O}",
            $"Platform: {PlatformHelper.GetPlatformName()}",
            $"Authenticated: {IsAuthenticated}",
            $"CanAuthenticate: {CanAuthenticate}",
            $"Is Syncing: {IsSyncing}",
            $"Status: {SyncStatus}"
        };

        if (_googleDriveAuthService != null)
        {
            lines.Add($"Current User: {_googleDriveAuthService.CurrentUserEmail ?? "(none)"}");
        }

        if (PlatformHelper.IsAndroid)
        {
            lines.Add($"Android LaunchUri set: {AndroidOAuthCallbackBridge.LaunchUri != null}");
        }

        if (_googleDriveSyncService != null)
        {
            var status = _googleDriveSyncService.CurrentStatus;
            lines.Add($"Last Sync Time: {status.LastSyncTime?.ToString("O") ?? "Never"}");
            lines.Add($"Pending Items: {status.PendingItemsCount}");
            lines.Add($"Progress: {status.ProgressPercentage}%");
        }

        if (_syncQueueManager != null)
        {
            try
            {
                var queueCount = await _syncQueueManager.GetPendingCountAsync().ConfigureAwait(false);
                lines.Add($"Queue (local pending): {queueCount}");
            }
            catch (Exception ex)
            {
                lines.Add($"Queue read error: {ex.Message}");
            }
        }

        if (_localStorageProvider != null)
        {
            try
            {
                var progressJson = await _localStorageProvider.GetAsync("CurrentReadingProgress").ConfigureAwait(false);
                var prefsJson = await _localStorageProvider.GetAsync("UserPreferences").ConfigureAwait(false);

                lines.Add("--- Local Sync Data ---");
                lines.Add($"CurrentReadingProgress: {(string.IsNullOrWhiteSpace(progressJson) ? "(empty)" : progressJson)}");
                lines.Add($"UserPreferences: {(string.IsNullOrWhiteSpace(prefsJson) ? "(empty)" : prefsJson)}");
            }
            catch (Exception ex)
            {
                lines.Add($"Local storage read error: {ex.Message}");
            }
        }

        if (_googleDriveSyncService != null && IsAuthenticated)
        {
            try
            {
                var remoteData = await _googleDriveSyncService.GetUserDataAsync().ConfigureAwait(false);
                var remoteAnnotations = await _googleDriveSyncService.GetAllAnnotationsAsync().ConfigureAwait(false);

                lines.Add("--- Remote Sync Data (Google Drive appDataFolder) ---");
                lines.Add($"Remote Reading Progress: {(remoteData?.ReadingProgress != null ? $"{remoteData.ReadingProgress.BookCode} {remoteData.ReadingProgress.Chapter}:{remoteData.ReadingProgress.Verse}" : "none")}");
                lines.Add($"Remote Annotations Entries: {remoteAnnotations.Count}");
                lines.Add($"Remote Preferences Exists: {remoteData?.Preferences != null}");
            }
            catch (Exception ex)
            {
                lines.Add($"Remote read error: {ex.Message}");
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SyncDebugData = string.Join(Environment.NewLine, lines);
            SyncDebugLastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        });
    }

    public void ClearSyncDebugLogs()
    {
        SyncDebugLogs = [];
        AppendSyncDebugLog("Logs cleared.");
    }

    public async Task ClearRemoteDataAsync()
    {
        if (_googleDriveSyncService == null || !IsAuthenticated)
        {
            AppendSyncDebugLog("Cannot clear remote data: not authenticated.");
            return;
        }

        AppendSyncDebugLog("Clearing all remote data from Google Drive...");
        var success = await _googleDriveSyncService.ClearRemoteSyncDataAsync().ConfigureAwait(false);
        AppendSyncDebugLog(success
            ? "Remote data cleared successfully."
            : "Failed to clear remote data.");

        await RefreshSyncDebugDataAsync().ConfigureAwait(false);
    }

    public void AppendSyncDebugLog(string message)
    {
        var logs = SyncDebugLogs.ToList();
        var line = $"{DateTime.Now:HH:mm:ss} | {message}";
        logs.Add(line);

        const int maxLogEntries = 200;
        if (logs.Count > maxLogEntries)
            logs = logs.Skip(logs.Count - maxLogEntries).ToList();

        SyncDebugLogs = logs;
        DebugLogOverlayLines = logs.Count <= DebugOverlayMaxLines
            ? logs
            : logs.Skip(logs.Count - DebugOverlayMaxLines).ToList();

        // Add to observable overlay and schedule auto-removal after 6s (animation is 5s)
        Dispatcher.UIThread.Post(() =>
        {
            _debugLogOverlayItems.Add(line);
            while (_debugLogOverlayItems.Count > DebugOverlayMaxLines)
                _debugLogOverlayItems.RemoveAt(0);

            var capturedLine = line;
            DispatcherTimer.RunOnce(() =>
            {
                _debugLogOverlayItems.Remove(capturedLine);
            }, TimeSpan.FromSeconds(6));
        });
    }

    // ── Event Handlers ───────────────────────────────────────────────────────

    private void OnSyncProgress(object? sender, SyncProgressEventArgs e)
    {
        IsSyncing = e.IsSyncing;
        SyncStatus = e.Message;
        AppendSyncDebugLog($"[{e.Progress}%] {e.Message}");
        _ = RefreshSyncDebugDataAsync();

        if (e.IsCompleted)
            System.Diagnostics.Debug.WriteLine("Sync completed.");
        else if (e.IsError)
            System.Diagnostics.Debug.WriteLine($"Sync error: {e.Message}");
    }

    private void OnAuthStateChanged(bool isAuthenticated, string? userEmail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsAuthenticated = isAuthenticated;
            CurrentUserEmail = isAuthenticated ? userEmail : null;
            _ = RefreshSyncDebugDataAsync();
        });
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_syncCoordinator != null)
            _syncCoordinator.SyncProgress -= OnSyncProgress;

        if (_googleDriveAuthService != null)
            _googleDriveAuthService.AuthStateChanged -= OnAuthStateChanged;
    }

    // ── Private Types ────────────────────────────────────────────────────────

    private sealed class LocalTabStateData
    {
        public List<OpenTabReferenceState> Tabs { get; set; } = [];
        public int ActiveTabIndex { get; set; }
    }
}
