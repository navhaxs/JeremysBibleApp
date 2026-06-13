using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform;
using MyBibleApp.Services;
using MyBibleApp.Services.Sync;
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public class BibleReadingViewModel : ViewModelBase
{
    private const string BooksJsonUri     = "avares://MyBibleApp/Assets/books.json";
    private const string LastVerseJsonUri = "avares://MyBibleApp/Assets/last_verse.json";
    private const string StorageKey       = "BibleReadingProgress";

    private readonly List<BibleReadingBookEntry> _allBooks = [];
    private readonly ILocalStorageProvider?      _localStorageProvider;
    private readonly ISyncCoordinator?           _syncCoordinator;
    private readonly Task                        _initialLoadTask;

    public AppViewModel AppVM { get; }

    public IReadOnlyList<BibleReadingBookEntry> OtBooks { get; }
    public IReadOnlyList<BibleReadingBookEntry> NtBooks { get; }
    public IReadOnlyList<BibleReadingBookEntry> AllBooks => _allBooks;

    private DateTime? _lastUpdated;
    /// <summary>The local date/time when reading progress was last saved or loaded from storage.</summary>
    public DateTime? LastUpdated
    {
        get => _lastUpdated;
        private set => this.RaiseAndSetIfChanged(ref _lastUpdated, value);
    }

    public bool IsDebugMode => AppVM.IsDebugMode;

    private string _syncDebugInfo = string.Empty;
    public string SyncDebugInfo
    {
        get => _syncDebugInfo;
        private set => this.RaiseAndSetIfChanged(ref _syncDebugInfo, value);
    }

    /// <summary>Highlights the specified book/chapter as the currently viewed chapter.</summary>
    public void SetCurrentChapter(string? bookCode, int chapter)
    {
        foreach (var book in _allBooks)
        {
            var isMatchingBook = string.Equals(book.Code, bookCode, StringComparison.OrdinalIgnoreCase);
            foreach (var cell in book.Chapters)
                cell.IsCurrentChapter = isMatchingBook && cell.Number == chapter;
        }
    }

    public BibleReadingViewModel(AppViewModel appVM)
    {
        AppVM = appVM;
        AppVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppViewModel.IsDebugMode)) return;
            this.RaisePropertyChanged(nameof(IsDebugMode));
            if (AppVM.IsDebugMode) _ = RefreshSyncDebugInfoAsync();
        };

        try
        {
            var runtime = SharedSyncRuntime.Instance;
            _localStorageProvider = runtime.LocalStorageProvider;
            _syncCoordinator      = runtime.SyncCoordinator;
        }
        catch { /* sync not available */ }

        _allBooks.AddRange(LoadBooks());

        // OT = first 39 canonical books, NT = remaining 27
        OtBooks = _allBooks.Take(39).ToList();
        NtBooks = _allBooks.Skip(39).ToList();

        _initialLoadTask = LoadReadStateAsync();
    }

    /// <summary>Persist current read state locally and push to cloud sync queue.</summary>
    public async Task SaveAsync()
    {
        var data = BuildReadChaptersDict();
        await SaveReadStateAsync(data).ConfigureAwait(false);
        _ = PushToSyncAsync(data);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => LastUpdated = DateTime.Now);
        if (AppVM.IsDebugMode) _ = RefreshSyncDebugInfoAsync();
    }

    /// <summary>Apply a snapshot pulled from Google Drive (last-write-wins is handled by SyncCoordinator).</summary>
    public async Task ApplyRemoteSnapshotAsync(BibleReadingProgressSnapshot snapshot)
    {
        // Wait for local load to finish first — avoids race where LoadReadStateAsync stomps remote data.
        await _initialLoadTask.ConfigureAwait(false);

        foreach (var book in _allBooks)
        {
            if (!snapshot.ReadChapters.TryGetValue(book.Code, out var readChapters)) continue;
            var readSet = new HashSet<int>(readChapters);
            foreach (var cell in book.Chapters)
                cell.IsRead = readSet.Contains(cell.Number);
        }
        // Persist the applied state locally so it survives restart without re-pulling.
        _ = SaveReadStateAsync(BuildReadChaptersDict());
        if (AppVM.IsDebugMode) _ = RefreshSyncDebugInfoAsync();
    }

    public async Task RefreshSyncDebugInfoAsync()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("── Bible Reading Sync Debug ─────────────");

            if (_localStorageProvider == null)
            {
                sb.Append("Storage: unavailable");
                SyncDebugInfo = sb.ToString();
                return;
            }

            // Local snapshot
            var local = await _localStorageProvider
                .GetObjectAsync<BibleReadingProgressSnapshot>(StorageKey)
                .ConfigureAwait(false);
            if (local != null)
            {
                var count = local.ReadChapters?.Values.Sum(v => v.Length) ?? 0;
                sb.AppendLine($"Local snapshot: {count} chapters read");
                sb.AppendLine($"Local LastModified: {local.LastModified:u}");
            }
            else
            {
                sb.AppendLine("Local snapshot: null");
            }

            // Cached Drive modifiedTime
            var driveModTime = await _localStorageProvider
                .GetAsync("DriveModTime_user_data.json")
                .ConfigureAwait(false);
            sb.AppendLine($"Drive mod time (cached): {driveModTime ?? "null — user_data.json never synced"}");

            // Pending queue items
            try
            {
                var ops = await SharedSyncRuntime.Instance.SyncQueueManager
                    .GetPendingOperationsAsync()
                    .ConfigureAwait(false);
                var brCount = ops.Count(o => o.OperationType == "BibleReadingProgress");
                var totalCount = ops.Count;
                sb.AppendLine($"Queue: {brCount} BibleReadingProgress pending ({totalCount} total)");
            }
            catch
            {
                sb.AppendLine("Queue: unavailable");
            }

            // Auth state
            var authenticated = _syncCoordinator != null;
            sb.Append($"Sync coordinator: {(authenticated ? "available" : "null")}");

            SyncDebugInfo = sb.ToString();
        }
        catch (Exception ex)
        {
            SyncDebugInfo = $"Debug info error: {ex.Message}";
        }
    }

    private Dictionary<string, int[]> BuildReadChaptersDict() =>
        _allBooks.ToDictionary(
            b => b.Code,
            b => b.Chapters.Where(c => c.IsRead).Select(c => c.Number).ToArray());

    private async Task PushToSyncAsync(Dictionary<string, int[]> data)
    {
        try
        {
            if (_syncCoordinator == null) return;
            await _syncCoordinator.SyncBibleReadingProgressAsync(data).ConfigureAwait(false);
        }
        catch { /* ignore sync errors */ }
    }

    private IEnumerable<BibleReadingBookEntry> LoadBooks()
    {
        Dictionary<string, int> chapterCounts;
        Dictionary<string, string> names;
        List<string> orderedCodes;

        try
        {
            chapterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var lastVerseDoc = ReadJsonAsset(LastVerseJsonUri);
            foreach (var prop in lastVerseDoc.RootElement.EnumerateObject())
                chapterCounts[prop.Name] = prop.Value.GetArrayLength();

            names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            orderedCodes = [];

            using var booksDoc = ReadJsonAsset(BooksJsonUri);
            if (booksDoc.RootElement.TryGetProperty("book_names_english", out var namesEl))
                foreach (var p in namesEl.EnumerateObject())
                    names[p.Name] = p.Value.GetString() ?? p.Name;

            if (!booksDoc.RootElement.TryGetProperty("books_ordered", out var orderedEl))
                yield break;

            foreach (var item in orderedEl.EnumerateArray())
            {
                var code = item.GetString();
                if (!string.IsNullOrWhiteSpace(code))
                    orderedCodes.Add(code);
            }
        }
        catch { yield break; }

        var index = 0;
        foreach (var code in orderedCodes)
        {
            var name     = names.TryGetValue(code, out var n) ? n : code;
            var chapters = chapterCounts.TryGetValue(code, out var c) ? c : 1;
            var isOt     = index < 39;
            var bookIndex = isOt ? index : Math.Max(0, index - 39);
            yield return new BibleReadingBookEntry(code, name, chapters, isOt, bookIndex);
            index++;
        }
    }

    private async Task LoadReadStateAsync()
    {
        try
        {
            if (_localStorageProvider == null) return;
            var snapshot = await _localStorageProvider
                .GetObjectAsync<BibleReadingProgressSnapshot>(StorageKey)
                .ConfigureAwait(false);
            if (snapshot?.ReadChapters == null) return;

            foreach (var book in _allBooks)
            {
                if (!snapshot.ReadChapters.TryGetValue(book.Code, out var readChapters)) continue;
                var readSet = new HashSet<int>(readChapters);
                foreach (var cell in book.Chapters)
                    cell.IsRead = readSet.Contains(cell.Number);
            }
        }
        catch { /* ignore persistence errors */ }
    }

    private async Task SaveReadStateAsync(Dictionary<string, int[]> data)
    {
        try
        {
            if (_localStorageProvider == null) return;
            var snapshot = new BibleReadingProgressSnapshot { ReadChapters = data };
            await _localStorageProvider
                .SaveObjectAsync(StorageKey, snapshot)
                .ConfigureAwait(false);
        }
        catch { /* ignore persistence errors */ }
    }

    private static JsonDocument ReadJsonAsset(string assetUri)
    {
        var uri = new Uri(assetUri, UriKind.Absolute);
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        return JsonDocument.Parse(reader.ReadToEnd());
    }
}
