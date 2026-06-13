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
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public class ScriptureViewModel : ViewModelBase, IDisposable
{
    private const string SampleUsxUri = "avares://MyBibleApp/Assets/usx/sample-jhn1.usx";
    private const string BooksJsonUri = "avares://MyBibleApp/Assets/books.json";
    private const string LastVerseJsonUri = "avares://MyBibleApp/Assets/last_verse.json";

    // Lookup metadata is identical for every VM — load once, share across all instances.
    private static readonly Lazy<LookupMetadataCache> SharedLookupMetadata =
        new(LoadLookupMetadataOnce, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed class LookupMetadataCache
    {
        public IReadOnlyList<ScriptureLookupBook> Books { get; init; } = [];
        public Dictionary<string, int[]> ChapterVerseIndex { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly IBookNameProvider _bookNameProvider;
    private readonly BibleContentService _bibleContent;
    private readonly Dictionary<string, int[]> _chapterVerseIndex;

    private string _header = string.Empty;
    private string _bookTitle = string.Empty;
    private string _bookCode = string.Empty;
    private string _status = string.Empty;
    private IReadOnlyList<BibleParagraph> _paragraphs = [];

    private IReadOnlyList<ScriptureLookupBook> _lookupBooks = [];
    private IReadOnlyList<int> _lookupChapters = [];
    private IReadOnlyList<int> _lookupVerses = [];
    private ScriptureLookupBook? _selectedLookupBook;
    private int _selectedLookupChapter = 1;
    private int _selectedLookupVerse = 1;
    private bool _preserveVerseOnChapterRefresh;
    private bool _preserveChapterOnBookChange;

    private CancellationTokenSource? _readingProgressSyncCts;
    private CancellationTokenSource? _sampleLoadCts;

    public AppViewModel AppVM { get; }

    public ScriptureViewModel(AppViewModel appVM)
    {
        AppVM = appVM;
        _bookNameProvider = new JsonBookNameProvider(BooksJsonUri);
        _bibleContent = BibleContentService.Instance;

        // Apply from static cache — no file I/O on UI thread after first VM.
        var cache = SharedLookupMetadata.Value;
        _chapterVerseIndex = cache.ChapterVerseIndex;
        LookupBooks = cache.Books;

        // Load sample content on a thread pool thread so the UI thread is never blocked.
        // Cancelled as soon as real content begins loading (TryLoadBookFromApiAsync).
        _sampleLoadCts = new CancellationTokenSource();
        var ct = _sampleLoadCts.Token;
        _ = Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var loader = new UsxBibleAssetLoader(new UsxBibleParser());
                var book = loader.LoadFromAsset(SampleUsxUri);
                if (ct.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ct.IsCancellationRequested)
                        ApplyLoadedBook(book, "Loaded from local USX asset.", 1, 1);
                });
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() =>
                {
                    Header = "Bible content unavailable";
                    Status = $"Failed to load USX asset: {ex.Message}";
                    Paragraphs = [];
                });
            }
        }, ct);
    }

    // ── Passage State ────────────────────────────────────────────────────────

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

    public IReadOnlyList<BibleParagraph> Paragraphs
    {
        get => _paragraphs;
        private set => this.RaiseAndSetIfChanged(ref _paragraphs, value);
    }

    // ── Chapter/Verse Navigation ─────────────────────────────────────────────

    public bool CanGoToPreviousChapter => _selectedLookupChapter > 1;

    public bool CanGoToNextChapter =>
        _lookupChapters.Count > 0 && _selectedLookupChapter < _lookupChapters[^1];

    public void GoToPreviousChapter()
    {
        if (CanGoToPreviousChapter)
            SelectedLookupChapter--;
    }

    public void GoToNextChapter()
    {
        if (CanGoToNextChapter)
            SelectedLookupChapter++;
    }

    // ── Lookup State ─────────────────────────────────────────────────────────

    public IReadOnlyList<ScriptureLookupBook> LookupBooks
    {
        get => _lookupBooks;
        private set => this.RaiseAndSetIfChanged(ref _lookupBooks, value);
    }

    public IReadOnlyList<int> LookupChapters
    {
        get => _lookupChapters;
        private set
        {
            this.RaiseAndSetIfChanged(ref _lookupChapters, value);
            this.RaisePropertyChanged(nameof(CanGoToNextChapter));
        }
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

            Dispatcher.UIThread.Post(() =>
            {
                if (!Equals(_selectedLookupBook, value))
                    return;

                var chapterCount = GetChapterCount(value.Code);
                LookupChapters = Enumerable.Range(1, chapterCount).ToArray();

                if (!_preserveChapterOnBookChange)
                    SelectedLookupChapter = 1;
                _preserveChapterOnBookChange = false;
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
            this.RaisePropertyChanged(nameof(CanGoToPreviousChapter));
            this.RaisePropertyChanged(nameof(CanGoToNextChapter));
            DebounceReadingProgressSync();

            if (_preserveVerseOnChapterRefresh)
            {
                _preserveVerseOnChapterRefresh = false;
                RefreshVerses(resetToFirstVerse: false);
                return;
            }

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

    // ── Position Management ──────────────────────────────────────────────────

    public void UpdateLookupFromReaderProgress(int chapter, int verse)
    {
        _preserveVerseOnChapterRefresh = true;
        SelectedLookupChapter = Math.Max(1, chapter);
        SelectedLookupVerse = Math.Max(1, verse);
    }

    /// <summary>
    /// Atomically sets book + chapter + verse for tab restore.
    /// Prevents the deferred chapter-reset (triggered by book change) from
    /// clobbering the chapter we're restoring.
    /// </summary>
    public void RestoreLookupPosition(ScriptureLookupBook? book, int chapter, int verse)
    {
        if (book != null)
            _preserveChapterOnBookChange = true;
        SelectedLookupBook = book;
        _preserveVerseOnChapterRefresh = true;
        SelectedLookupChapter = Math.Max(1, chapter);
        SelectedLookupVerse = Math.Max(1, verse);
    }

    // ── Book Loading ─────────────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> TryLoadBookFromApiAsync(string bookCode, int chapter, int verse)
    {
        // Cancel the background sample load so it can't overwrite real content.
        _sampleLoadCts?.Cancel();
        _sampleLoadCts?.Dispose();
        _sampleLoadCts = null;

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

    // ── Disposal ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _sampleLoadCts?.Cancel();
        _sampleLoadCts?.Dispose();
        _sampleLoadCts = null;
        _readingProgressSyncCts?.Cancel();
        _readingProgressSyncCts?.Dispose();
    }

    // ── Private Helpers ──────────────────────────────────────────────────────

    private void ApplyLoadedBook(BibleBook book, string sourceStatus, int initialSelectionChapter, int initialSelectionVerse)
    {
        _bookCode = book.Code;
        _bookTitle = _bookNameProvider.GetEnglishName(book.Code);

        Paragraphs = book.Paragraphs;
        Status = $"Loaded {book.VerseCount} verses across {book.Paragraphs.Count} paragraphs. {sourceStatus}";

        var initialBook = _lookupBooks.FirstOrDefault(b =>
            string.Equals(b.Code, _bookCode, StringComparison.OrdinalIgnoreCase));

        if (initialBook != null)
            _preserveChapterOnBookChange = true;

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

    private static LookupMetadataCache LoadLookupMetadataOnce()
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

            var chapterVerseIndex = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in lastVerseDocument.RootElement.EnumerateObject())
            {
                var verses = property.Value.EnumerateArray()
                    .Select(v => v.TryGetInt32(out var n) ? n : 1)
                    .ToArray();
                chapterVerseIndex[property.Name] = verses;
            }

            var books = new List<ScriptureLookupBook>();
            foreach (var code in orderedCodes)
            {
                var name = nameLookup.TryGetValue(code, out var english) ? english : code;
                books.Add(new ScriptureLookupBook(code, name));
            }

            return new LookupMetadataCache { Books = books, ChapterVerseIndex = chapterVerseIndex };
        }
        catch
        {
            return new LookupMetadataCache();
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

    private void DebounceReadingProgressSync()
    {
        if (AppVM.SuppressReadingProgressSync)
            return;

        _readingProgressSyncCts?.Cancel();
        _readingProgressSyncCts?.Dispose();

        _readingProgressSyncCts = new CancellationTokenSource();
        var token = _readingProgressSyncCts.Token;

        _ = Task.Delay(500, token).ContinueWith(async _ =>
        {
            if (!token.IsCancellationRequested)
            {
                var bookCode = _selectedLookupBook?.Code ?? _bookCode;
                await AppVM.SyncReadingProgressAsync(bookCode, Math.Max(1, _selectedLookupChapter), Math.Max(1, _selectedLookupVerse)).ConfigureAwait(false);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
