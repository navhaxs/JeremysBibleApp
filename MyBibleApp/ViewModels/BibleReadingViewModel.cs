using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public BibleReadingViewModel()
    {
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

        _ = LoadReadStateAsync();
    }

    /// <summary>Persist current read state locally and push to cloud sync queue.</summary>
    public async Task SaveAsync()
    {
        var data = BuildReadChaptersDict();
        await SaveReadStateAsync(data).ConfigureAwait(false);
        _ = PushToSyncAsync(data);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => LastUpdated = DateTime.Now);
    }

    /// <summary>Apply a snapshot pulled from Google Drive (last-write-wins is handled by SyncCoordinator).</summary>
    public void ApplyRemoteSnapshot(BibleReadingProgressSnapshot snapshot)
    {
        foreach (var book in _allBooks)
        {
            if (!snapshot.ReadChapters.TryGetValue(book.Code, out var readChapters)) continue;
            var readSet = new HashSet<int>(readChapters);
            foreach (var cell in book.Chapters)
                cell.IsRead = readSet.Contains(cell.Number);
        }
        // Persist the applied state locally so it survives restart without re-pulling.
        _ = SaveReadStateAsync(BuildReadChaptersDict());
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

        foreach (var code in orderedCodes)
        {
            var name     = names.TryGetValue(code, out var n) ? n : code;
            var chapters = chapterCounts.TryGetValue(code, out var c) ? c : 1;
            yield return new BibleReadingBookEntry(code, name, chapters);
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
