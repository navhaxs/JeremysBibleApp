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
    private const string TabBarVisibleKey = "IsTabBarVisible";
    private const string ThemeKey = "SelectedThemeId";
    private const int DebugOverlayMaxLines = 12;

    private readonly ISyncCoordinator? _syncCoordinator;
    private readonly ISyncQueueManager? _syncQueueManager;
    private readonly IGoogleDriveSyncService? _googleDriveSyncService;
    private readonly IGoogleDriveAuthService? _googleDriveAuthService;
    private readonly ILocalStorageProvider? _localStorageProvider;

    private bool _isDebugMode;
    private bool _isTabBarVisible = true;
    private bool _isSyncing;
    private bool _isAuthenticated;
    private bool _isAuthenticating;
    private string? _currentUserEmail;
    private string _syncStatus = string.Empty;
    private DateTimeOffset? _lastUpToDateSyncAt;
    private DispatcherTimer? _syncStatusTimer;
    private EventHandler? _onLifecycleSuspended;
    private EventHandler? _onLifecycleResumed;
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
            _syncCoordinator.BibleReadingProgressSynced += OnBibleReadingProgressSynced;
            _googleDriveAuthService.AuthStateChanged += OnAuthStateChanged;
            IsAuthenticated = _googleDriveAuthService.IsAuthenticated;
            CurrentUserEmail = _googleDriveAuthService.CurrentUserEmail;

            SyncStatus = "Sync initialized";
            AppendSyncDebugLog("Sync services initialized.");

            _onLifecycleSuspended = (_, _) => StopSyncStatusTimer();
            _onLifecycleResumed = (_, _) =>
            {
                if (IsAuthenticated && _lastUpToDateSyncAt != null)
                    StartSyncStatusTimer();
            };
            AppLifecycleService.Instance.Suspended += _onLifecycleSuspended;
            AppLifecycleService.Instance.Resumed += _onLifecycleResumed;
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

    // ── Tab Bar Visible ──────────────────────────────────────────────────────

    public bool IsTabBarVisible
    {
        get => _isTabBarVisible;
        set
        {
            var old = _isTabBarVisible;
            this.RaiseAndSetIfChanged(ref _isTabBarVisible, value);
            if (old != _isTabBarVisible)
                _ = PersistTabBarVisibleAsync(_isTabBarVisible);
        }
    }

    private async Task PersistTabBarVisibleAsync(bool value)
    {
        if (_localStorageProvider == null) return;
        try
        {
            await _localStorageProvider.SaveAsync(TabBarVisibleKey, value ? "true" : "false").ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    public async Task LoadTabBarVisibleFromStorageAsync()
    {
        if (_localStorageProvider == null) return;
        try
        {
            var stored = await _localStorageProvider.GetAsync(TabBarVisibleKey).ConfigureAwait(false);
            if (string.Equals(stored, "false", StringComparison.OrdinalIgnoreCase))
                await Dispatcher.UIThread.InvokeAsync(() => _isTabBarVisible = false);
            this.RaisePropertyChanged(nameof(IsTabBarVisible));
        }
        catch { /* best-effort */ }
    }

    // ── Theme ────────────────────────────────────────────────────────────────

    private string _selectedThemeId = Models.AppTheme.LightWhite.Id;

    public string SelectedThemeId
    {
        get => _selectedThemeId;
        set
        {
            if (_selectedThemeId == value) return;
            this.RaiseAndSetIfChanged(ref _selectedThemeId, value);
            _ = PersistThemeAsync(value);
        }
    }

    private async Task PersistThemeAsync(string themeId)
    {
        if (_localStorageProvider == null) return;
        try
        {
            await _localStorageProvider.SaveAsync(ThemeKey, themeId).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    public async Task LoadThemeFromStorageAsync()
    {
        if (_localStorageProvider == null) return;
        try
        {
            var stored = await _localStorageProvider.GetAsync(ThemeKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stored))
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _selectedThemeId = stored;
                    this.RaisePropertyChanged(nameof(SelectedThemeId));
                });
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

    /// <summary>Raised when a force sync pull returns updated Bible reading progress for the UI to apply.</summary>
    public event EventHandler<BibleReadingProgressSnapshot>? BibleReadingProgressPulled;

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

        // General sync: pull from Drive + drain local queue.
        // BibleReadingProgressSynced event on the coordinator fires automatically when data is pulled.
        await _syncCoordinator.ForceSyncAsync().ConfigureAwait(false);

        // Explicit journal sync: push/pull journal data regardless of remote
        // timestamp so local journal changes are never left behind.
        await _syncCoordinator.SyncJournalDataAsync().ConfigureAwait(false);
    }

    public async Task SyncJournalNowAsync()
    {
        if (_syncCoordinator == null || !IsAuthenticated)
        {
            AppendSyncDebugLog("Cannot sync journal: not authenticated.");
            return;
        }
        AppendSyncDebugLog("Manual journal sync requested.");
        var result = await _syncCoordinator.SyncJournalDataAsync().ConfigureAwait(false);
        AppendSyncDebugLog(result.IsSuccess
            ? $"Journal sync succeeded ({result.ItemsSynced} item(s))."
            : $"Journal sync failed: {result.ErrorMessage}");
        await RefreshSyncDebugDataAsync().ConfigureAwait(false);
    }

    public async Task<bool> HasPendingLocalSyncChangesAsync()
    {
        if (!IsAuthenticated || _syncQueueManager == null)
            return false;

        try
        {
            var pendingCount = await _syncQueueManager.GetPendingCountAsync().ConfigureAwait(false);
            return pendingCount > 0;
        }
        catch (Exception ex)
        {
            AppendSyncDebugLog($"Failed to determine pending sync queue count: {ex.Message}");
            return false;
        }
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

        if (_localStorageProvider != null)
        {
            try
            {
                var journalModTime = await _localStorageProvider.GetAsync("DriveModTime_journals.json").ConfigureAwait(false);
                var userDataModTime = await _localStorageProvider.GetAsync("DriveModTime_user_data.json").ConfigureAwait(false);
                lines.Add("--- Cached Drive Mod Times ---");
                lines.Add($"journals.json:   {(string.IsNullOrWhiteSpace(journalModTime) ? "(none)" : journalModTime)}");
                lines.Add($"user_data.json:  {(string.IsNullOrWhiteSpace(userDataModTime) ? "(none)" : userDataModTime)}");
            }
            catch (Exception ex)
            {
                lines.Add($"Cached mod time read error: {ex.Message}");
            }
        }

        if (_googleDriveSyncService != null && IsAuthenticated && !IsSyncing)
        {
            try
            {
                var remoteData = await _googleDriveSyncService.GetUserDataAsync().ConfigureAwait(false);
                var remoteAnnotations = await _googleDriveSyncService.GetAllAnnotationsAsync().ConfigureAwait(false);

                lines.Add("--- Remote Sync Data (Google Drive appDataFolder) ---");
                lines.Add($"Remote Reading Progress: {(remoteData?.ReadingProgress != null ? $"{remoteData.ReadingProgress.BookCode} {remoteData.ReadingProgress.Chapter}:{remoteData.ReadingProgress.Verse}" : "none")}");
                var brSnap = remoteData?.BibleReadingProgress;
                lines.Add($"Remote Bible Reading Progress: {(brSnap != null ? $"{brSnap.ReadChapters?.Values.Sum(v => v.Length) ?? 0} chapters read, LastModified {brSnap.LastModified:u}" : "none")}");
                lines.Add($"Remote Annotations Entries: {remoteAnnotations.Count}");
                lines.Add($"Remote Preferences Exists: {remoteData?.Preferences != null}");
            }
            catch (Exception ex)
            {
                lines.Add($"Remote read error: {ex.Message}");
            }

            try
            {
                var journalJson = IsSyncing ? null : await _googleDriveSyncService.GetJournalDataAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(journalJson))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(journalJson);
                    if (doc.RootElement.TryGetProperty("journals", out var arr)
                        && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        lines.Add($"Remote Journals: {arr.GetArrayLength()} journal(s)  ({journalJson.Length:N0} bytes)");
                        var n = 0;
                        foreach (var j in arr.EnumerateArray())
                        {
                            if (n++ >= 3) break;
                            var hasMeta = j.TryGetProperty("metadata", out var meta);
                            var name = hasMeta && meta.TryGetProperty("name", out var np) ? np.GetString() ?? "?" : "?";
                            var modified = hasMeta && meta.TryGetProperty("lastModifiedUtc", out var mp) ? mp.GetString() ?? "?" : "?";
                            var shortName = name.Length > 28 ? name[..28] + "…" : name;
                            var shortMod = modified.Length > 20 ? modified[..20] : modified;
                            lines.Add($"  · \"{shortName}\"  {shortMod}");
                        }
                        if (arr.GetArrayLength() > 3)
                            lines.Add($"  … and {arr.GetArrayLength() - 3} more");
                    }
                    else
                    {
                        lines.Add($"Remote Journals: (non-standard format, {journalJson.Length:N0} bytes)");
                    }
                }
                else
                {
                    lines.Add("Remote Journals: (none on Drive)");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"Remote journal read error: {ex.Message}");
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

    private void OnBibleReadingProgressSynced(object? sender, BibleReadingProgressSnapshot snapshot)
    {
        BibleReadingProgressPulled?.Invoke(this, snapshot);
    }

    private void OnSyncProgress(object? sender, SyncProgressEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsSyncing = e.IsSyncing;
            AppendSyncDebugLog($"[{e.Progress}%] {e.Message}");

            // Only refresh the full debug panel (which makes remote Drive calls) when sync
            // completes. Calling it on every progress event floods the thread pool with
            // concurrent network requests and causes ANR on Android.
            if (e.IsCompleted || e.IsError)
                _ = RefreshSyncDebugDataAsync();

            if (e.IsSyncing)
                StopSyncStatusTimer();

            if (e.IsCompleted && e.Message.Contains("up to date", StringComparison.OrdinalIgnoreCase))
            {
                _lastUpToDateSyncAt = DateTimeOffset.Now;
                SyncStatus = FormatUpToDateStatus();
                StartSyncStatusTimer();
            }
            else
            {
                SyncStatus = e.Message;
            }

            if (e.IsCompleted)
                System.Diagnostics.Debug.WriteLine("Sync completed.");
            else if (e.IsError)
                System.Diagnostics.Debug.WriteLine($"Sync error: {e.Message}");
        });
    }

    private string FormatUpToDateStatus()
    {
        if (_lastUpToDateSyncAt == null) return "Sync complete — up to date";
        var elapsed = DateTimeOffset.Now - _lastUpToDateSyncAt.Value;
        return elapsed.TotalMinutes < 1
            ? "Sync complete — up to date"
            : $"Sync complete — {(int)elapsed.TotalMinutes} min ago";
    }

    private void StartSyncStatusTimer()
    {
        if (_syncStatusTimer != null) return;
        _syncStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _syncStatusTimer.Tick += (_, _) => SyncStatus = FormatUpToDateStatus();
        _syncStatusTimer.Start();
    }

    private void StopSyncStatusTimer()
    {
        _syncStatusTimer?.Stop();
        _syncStatusTimer = null;
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
        StopSyncStatusTimer();

        if (_syncCoordinator != null)
        {
            _syncCoordinator.SyncProgress -= OnSyncProgress;
            _syncCoordinator.BibleReadingProgressSynced -= OnBibleReadingProgressSynced;
        }

        if (_googleDriveAuthService != null)
            _googleDriveAuthService.AuthStateChanged -= OnAuthStateChanged;

        if (_onLifecycleSuspended != null)
            AppLifecycleService.Instance.Suspended -= _onLifecycleSuspended;
        if (_onLifecycleResumed != null)
            AppLifecycleService.Instance.Resumed -= _onLifecycleResumed;
    }

    // ── Private Types ────────────────────────────────────────────────────────

    private sealed class LocalTabStateData
    {
        public List<OpenTabReferenceState> Tabs { get; set; } = [];
        public int ActiveTabIndex { get; set; }
    }
}
