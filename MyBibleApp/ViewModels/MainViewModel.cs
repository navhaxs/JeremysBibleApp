using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Threading;
using MyBibleApp.Models;
using MyBibleApp.Services;
using MyBibleApp.Services.Sync;
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const string SampleUsxUri = "avares://MyBibleApp/Assets/usx/sample-jhn1.usx";
    private const string BooksJsonUri = "avares://MyBibleApp/Assets/books.json";
    private const string LastVerseJsonUri = "avares://MyBibleApp/Assets/last_verse.json";
    private const string OpenTabsPreferenceKey = "open_tabs_v1";
    private const string ActiveTabIndexPreferenceKey = "active_tab_index";
    private const string OpenTabsUpdatedAtPreferenceKey = "open_tabs_updated_utc";
    private CancellationTokenSource? _readingProgressSyncCts;
    private bool _suppressReadingProgressSync;

    private string _header = string.Empty;
    private string _bookTitle = string.Empty;
    private string _bookCode = string.Empty;
    private readonly IBookNameProvider _bookNameProvider;
    private readonly BibleContentService _bibleContent;
    private string _status = string.Empty;

    private IReadOnlyList<BibleParagraph> _paragraphs = [];

    private IReadOnlyList<ScriptureLookupBook> _lookupBooks = [];
    private IReadOnlyList<int> _lookupChapters = [];
    private IReadOnlyList<int> _lookupVerses = [];
    private ScriptureLookupBook? _selectedLookupBook;
    private int _selectedLookupChapter = 1;
    private int _selectedLookupVerse = 1;
    private bool _preserveVerseOnChapterRefresh;

    private readonly Dictionary<string, int[]> _chapterVerseIndex = new(StringComparer.OrdinalIgnoreCase);

// #if DEBUG
//     private bool _isDebugMode = true;
// #else
    private bool _isDebugMode;
// #endif

    private readonly ISyncCoordinator? _syncCoordinator;
    private readonly ISyncQueueManager? _syncQueueManager;
    private readonly IGoogleDriveSyncService? _googleDriveSyncService;
    private readonly IGoogleDriveAuthService? _googleDriveAuthService;
    private readonly ILocalStorageProvider? _localStorageProvider;
    private string _syncStatus = string.Empty;
    private bool _isSyncing;
    private bool _isAuthenticated;
    private bool _isAuthenticating;
    private string? _currentUserEmail;
    private IReadOnlyList<string> _syncDebugLogs = [];
    private string _syncDebugData = string.Empty;
    private string _syncDebugLastUpdated = "Never";

    public MainViewModel()
    {
        _bookNameProvider = new JsonBookNameProvider(BooksJsonUri);
        _bibleContent = BibleContentService.Instance;

        // Initialize sync services
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

        LoadLookupMetadata();

        try
        {
            var loader = new UsxBibleAssetLoader(new UsxBibleParser());
            var book = loader.LoadFromAsset(SampleUsxUri);
            ApplyLoadedBook(book, "Loaded from local USX asset.", initialSelectionChapter: 1, initialSelectionVerse: 1);
        }
        catch (Exception ex)
        {
            Header = "Bible content unavailable";
            Status = $"Failed to load USX asset: {ex.Message}";
            Paragraphs = [];
        }
    }

    public string Header
    {
        get => _header;
        set => this.RaiseAndSetIfChanged(ref _header, value);
    }

    public string BookTitle => _bookTitle;

    public string BookCode => _bookCode;

    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool IsDebugMode
    {
        get => _isDebugMode;
        set => this.RaiseAndSetIfChanged(ref _isDebugMode, value);
    }

    public IReadOnlyList<BibleParagraph> Paragraphs
    {
        get => _paragraphs;
        private set => this.RaiseAndSetIfChanged(ref _paragraphs, value);
    }

    public IReadOnlyList<ScriptureLookupBook> LookupBooks
    {
        get => _lookupBooks;
        private set => this.RaiseAndSetIfChanged(ref _lookupBooks, value);
    }

    public IReadOnlyList<int> LookupChapters
    {
        get => _lookupChapters;
        private set => this.RaiseAndSetIfChanged(ref _lookupChapters, value);
    }

    public IReadOnlyList<int> LookupVerses
    {
        get => _lookupVerses;
        private set => this.RaiseAndSetIfChanged(ref _lookupVerses, value);
    }

    public ScriptureLookupBook? SelectedLookupBook
    {
        get => _selectedLookupBook;
        set
        {
            if (Equals(_selectedLookupBook, value))
                return;

            this.RaiseAndSetIfChanged(ref _selectedLookupBook, value);
            if (value == null)
                return;

            // Defer source changes to avoid mutating a ListBox source while its
            // SelectionModel is still committing the current update.
            Dispatcher.UIThread.Post(() =>
            {
                if (!Equals(_selectedLookupBook, value))
                    return;

                var chapterCount = GetChapterCount(value.Code);
                LookupChapters = Enumerable.Range(1, chapterCount).ToArray();

                if (_selectedLookupChapter < 1 || _selectedLookupChapter > chapterCount)
                    SelectedLookupChapter = 1;
                else
                    RefreshVerses(resetToFirstVerse: true);
            });
        }
    }

    public int SelectedLookupChapter
    {
        get => _selectedLookupChapter;
        set
        {
            if (_selectedLookupChapter == value)
                return;

            this.RaiseAndSetIfChanged(ref _selectedLookupChapter, value);
            DebounceReadingProgressSync();

            if (_preserveVerseOnChapterRefresh)
            {
                _preserveVerseOnChapterRefresh = false;
                RefreshVerses(resetToFirstVerse: false);
                return;
            }

            // Same reason as above: defer verse source mutation until the current
            // chapter selection transaction has completed.
            Dispatcher.UIThread.Post(() =>
            {
                if (_selectedLookupChapter != value)
                    return;
                RefreshVerses(resetToFirstVerse: true);
            });
        }
    }

    public int SelectedLookupVerse
    {
        get => _selectedLookupVerse;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedLookupVerse, value);
            DebounceReadingProgressSync();
        }
    }

    /// <summary>
    /// Suppresses reading progress and preferences sync debounce — use during
    /// startup restore to prevent navigation and tab-state side-effects from
    /// enqueuing items before the cloud copy has been pulled down.
    /// </summary>
    public void SetReadingProgressSyncSuppressed(bool suppressed) =>
        _suppressReadingProgressSync = suppressed;

    public void UpdateLookupFromReaderProgress(int chapter, int verse)
    {
        _preserveVerseOnChapterRefresh = true;
        SelectedLookupChapter = Math.Max(1, chapter);
        SelectedLookupVerse = Math.Max(1, verse);
    }

    public async Task<(bool Success, string? Error)> TryLoadBookFromApiAsync(string bookCode, int chapter, int verse)
    {
        try
        {
            var book = await _bibleContent.LoadBookAsync(bookCode).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
                ApplyLoadedBook(book, "Loaded from fetch.bible API.", chapter, verse));

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private void ApplyLoadedBook(BibleBook book, string sourceStatus, int initialSelectionChapter, int initialSelectionVerse)
    {
        _bookCode = book.Code;
        _bookTitle = _bookNameProvider.GetEnglishName(book.Code);

        Paragraphs = book.Paragraphs;
        Status = $"Loaded {book.VerseCount} verses across {book.Paragraphs.Count} paragraphs. {sourceStatus}";

        var initialBook = _lookupBooks.FirstOrDefault(b =>
            string.Equals(b.Code, _bookCode, StringComparison.OrdinalIgnoreCase));
        if (initialBook != null)
            SelectedLookupBook = initialBook;
        else if (_lookupBooks.Count > 0)
            SelectedLookupBook = _lookupBooks[0];

        SelectedLookupChapter = Math.Max(1, initialSelectionChapter);
        SelectedLookupVerse = Math.Max(1, initialSelectionVerse);
        Header = $"{_bookTitle} {SelectedLookupChapter}:{SelectedLookupVerse}";
    }

    private void RefreshVerses(bool resetToFirstVerse)
    {
        var code = _selectedLookupBook?.Code;
        if (string.IsNullOrWhiteSpace(code))
        {
            LookupVerses = [];
            return;
        }

        var verseCount = GetVerseCount(code, _selectedLookupChapter);
        LookupVerses = Enumerable.Range(1, verseCount).ToArray();

        if (resetToFirstVerse || _selectedLookupVerse < 1 || _selectedLookupVerse > verseCount)
            SelectedLookupVerse = 1;
    }

    private int GetChapterCount(string code)
    {
        return _chapterVerseIndex.TryGetValue(code, out var versesByChapter)
            ? Math.Max(1, versesByChapter.Length)
            : 1;
    }

    private int GetVerseCount(string code, int chapter)
    {
        if (!_chapterVerseIndex.TryGetValue(code, out var versesByChapter) || versesByChapter.Length == 0)
            return 1;

        var chapterIndex = Math.Clamp(chapter - 1, 0, versesByChapter.Length - 1);
        return Math.Max(1, versesByChapter[chapterIndex]);
    }

    private void LoadLookupMetadata()
    {
        try
        {
            using var booksDocument = ReadJsonAsset(BooksJsonUri);
            using var lastVerseDocument = ReadJsonAsset(LastVerseJsonUri);

            var orderedCodes = new List<string>();
            if (booksDocument.RootElement.TryGetProperty("books_ordered", out var orderedElement))
            {
                foreach (var item in orderedElement.EnumerateArray())
                {
                    var code = item.GetString();
                    if (!string.IsNullOrWhiteSpace(code))
                        orderedCodes.Add(code);
                }
            }

            var nameLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (booksDocument.RootElement.TryGetProperty("book_names_english", out var namesElement))
            {
                foreach (var property in namesElement.EnumerateObject())
                    nameLookup[property.Name] = property.Value.GetString() ?? property.Name;
            }

            foreach (var property in lastVerseDocument.RootElement.EnumerateObject())
            {
                var verses = property.Value.EnumerateArray()
                    .Select(v => v.TryGetInt32(out var n) ? n : 1)
                    .ToArray();
                _chapterVerseIndex[property.Name] = verses;
            }

            var books = new List<ScriptureLookupBook>();
            foreach (var code in orderedCodes)
            {
                var name = nameLookup.TryGetValue(code, out var english) ? english : code;
                books.Add(new ScriptureLookupBook(code, name));
            }

            LookupBooks = books;
        }
        catch
        {
            LookupBooks = [];
        }
    }

    private static JsonDocument ReadJsonAsset(string assetUri)
    {
        var uri = new Uri(assetUri, UriKind.Absolute);
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonDocument.Parse(json);
    }

    private void OnSyncProgress(object? sender, SyncProgressEventArgs e)
    {
        // Update sync status and progress
        IsSyncing = e.IsSyncing;
        SyncStatus = e.Message;
        AppendSyncDebugLog($"[{e.Progress}%] {e.Message}");
        _ = RefreshSyncDebugDataAsync();

        // Optionally, handle specific sync events
        if (e.IsCompleted)
        {
            // Sync completed
            System.Diagnostics.Debug.WriteLine("Sync completed.");
        }
        else if (e.IsError)
        {
            // Sync error
            System.Diagnostics.Debug.WriteLine($"Sync error: {e.Message}");
        }
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

    public IReadOnlyList<string> SyncDebugLogs
    {
        get => _syncDebugLogs;
        private set => this.RaiseAndSetIfChanged(ref _syncDebugLogs, value);
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
        var result = await _syncCoordinator.PullFromDriveAsync().ConfigureAwait(false);

        if (result.IsSuccess && result.HadChanges)
            await ApplyPullResultAsync(result).ConfigureAwait(false);

        return result;
    }

    private async Task ApplyPullResultAsync(PullResult result)
    {
        if (result.ReadingProgress is { } rp && !string.IsNullOrWhiteSpace(rp.BookCode))
        {
            AppendSyncDebugLog($"Remote reading position is newer — navigating to {rp.BookCode} {rp.Chapter}:{rp.Verse}.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var book = _lookupBooks.FirstOrDefault(b =>
                    string.Equals(b.Code, rp.BookCode, StringComparison.OrdinalIgnoreCase));
                if (book != null)
                    SelectedLookupBook = book;
                UpdateLookupFromReaderProgress(rp.Chapter, rp.Verse);
            });
        }
    }

    public async Task SyncCurrentReadingProgressAsync()
    {
        if (_syncCoordinator == null)
            return;

        await SyncAuthStateWithServiceAsync().ConfigureAwait(false);

        if (!IsAuthenticated)
        {
            AppendSyncDebugLog("Skipping sync: not authenticated. Please click Authenticate first.");
            return;
        }

        var bookCode = _selectedLookupBook?.Code ?? _bookCode;
        if (string.IsNullOrWhiteSpace(bookCode))
            return;

        var result = await _syncCoordinator.SyncReadingProgressAsync(
            bookCode,
            Math.Max(1, _selectedLookupChapter),
            Math.Max(1, _selectedLookupVerse)
        ).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            AppendSyncDebugLog($"Failed to sync reading progress: {result.ErrorMessage}");
        }
    }

    public async Task PersistOpenTabReferencesAsync(IReadOnlyList<OpenTabReferenceState> openTabs, int activeTabIndex)
    {
        try
        {
            var preferences = await LoadOrCreatePreferencesSnapshotAsync().ConfigureAwait(false);

            preferences.CustomSettings[OpenTabsPreferenceKey] = JsonSerializer.Serialize(openTabs);
            preferences.CustomSettings[ActiveTabIndexPreferenceKey] = activeTabIndex.ToString();
            preferences.CustomSettings[OpenTabsUpdatedAtPreferenceKey] = DateTime.UtcNow.ToString("O");

            if (_suppressReadingProgressSync)
            {
                // During startup restore, only write locally — don't enqueue.
                // The cloud copy hasn't been pulled yet; queueing now would push
                // stale local data and lose any remote changes.
                if (_localStorageProvider != null)
                    await _localStorageProvider.SaveObjectAsync("UserPreferences", preferences).ConfigureAwait(false);
            }
            else if (_syncCoordinator != null)
            {
                var result = await _syncCoordinator.SyncPreferencesAsync(preferences).ConfigureAwait(false);
                if (!result.IsSuccess)
                    AppendSyncDebugLog($"Failed to persist open tabs to preferences: {result.ErrorMessage}");
            }
            else if (_localStorageProvider != null)
            {
                await _localStorageProvider.SaveObjectAsync("UserPreferences", preferences).ConfigureAwait(false);
            }

            AppendSyncDebugLog($"Stored {openTabs.Count} tab reference(s) in UserPreferences.");
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
            var preferences = await LoadOrCreatePreferencesSnapshotAsync().ConfigureAwait(false);

            if (!preferences.CustomSettings.TryGetValue(OpenTabsPreferenceKey, out var tabsJson)
                || string.IsNullOrWhiteSpace(tabsJson))
            {
                return ([], 0);
            }

            var tabs = JsonSerializer.Deserialize<List<OpenTabReferenceState>>(tabsJson) ?? [];
            tabs = tabs
                .Where(t => !string.IsNullOrWhiteSpace(t.BookCode) || !string.IsNullOrWhiteSpace(t.Header))
                .OrderBy(t => t.TabIndex)
                .ToList();

            var activeIndex = 0;
            if (preferences.CustomSettings.TryGetValue(ActiveTabIndexPreferenceKey, out var activeIndexRaw)
                && int.TryParse(activeIndexRaw, out var parsed))
            {
                activeIndex = parsed;
            }

            if (tabs.Count == 0)
                return ([], 0);

            activeIndex = Math.Clamp(activeIndex, 0, tabs.Count - 1);
            return (tabs, activeIndex);
        }
        catch (Exception ex)
        {
            AppendSyncDebugLog($"LoadPersistedOpenTabReferencesAsync error: {ex.Message}");
            return ([], 0);
        }
    }

    private async Task<PreferencesSnapshot> LoadOrCreatePreferencesSnapshotAsync()
    {
        if (_localStorageProvider == null)
            return new PreferencesSnapshot();

        var snapshot = await _localStorageProvider.GetObjectAsync<PreferencesSnapshot>("UserPreferences")
            .ConfigureAwait(false);

        return snapshot ?? new PreferencesSnapshot();
    }

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
        logs.Add($"{DateTime.Now:HH:mm:ss} | {message}");

        const int maxLogEntries = 200;
        if (logs.Count > maxLogEntries)
            logs = logs.Skip(logs.Count - maxLogEntries).ToList();

        SyncDebugLogs = logs;
    }

    public void Dispose()
    {
        if (_syncCoordinator != null)
            _syncCoordinator.SyncProgress -= OnSyncProgress;

        if (_googleDriveAuthService != null)
            _googleDriveAuthService.AuthStateChanged -= OnAuthStateChanged;

        _readingProgressSyncCts?.Cancel();
        _readingProgressSyncCts?.Dispose();
    }

    private void DebounceReadingProgressSync()
    {
        if (_suppressReadingProgressSync)
            return;

        _readingProgressSyncCts?.Cancel();
        _readingProgressSyncCts?.Dispose();

        _readingProgressSyncCts = new CancellationTokenSource();
        var token = _readingProgressSyncCts.Token;

        _ = Task.Delay(500, token).ContinueWith(async _ =>
        {
            if (!token.IsCancellationRequested)
                await SyncCurrentReadingProgressAsync().ConfigureAwait(false);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
