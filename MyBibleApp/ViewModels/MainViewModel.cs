using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Platform;
using MyBibleApp.Models;
using MyBibleApp.Services;
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const string SampleUsxUri = "avares://MyBibleApp/Assets/usx/sample-jhn1.usx";
    private const string BooksJsonUri = "avares://MyBibleApp/Assets/books.json";
    private const string LastVerseJsonUri = "avares://MyBibleApp/Assets/last_verse.json";

    private string _header = string.Empty;
    private string _bookTitle = string.Empty;
    private string _bookCode = string.Empty;
    private readonly IBookNameProvider _bookNameProvider;
    private string _status = string.Empty;

    private IReadOnlyList<ScriptureLookupBook> _lookupBooks = [];
    private IReadOnlyList<int> _lookupChapters = [];
    private IReadOnlyList<int> _lookupVerses = [];
    private ScriptureLookupBook? _selectedLookupBook;
    private int _selectedLookupChapter = 1;
    private int _selectedLookupVerse = 1;

    private readonly Dictionary<string, int[]> _chapterVerseIndex = new(StringComparer.OrdinalIgnoreCase);

// #if DEBUG
//     private bool _isDebugMode = true;
// #else
    private bool _isDebugMode;
// #endif

    public MainViewModel()
    {
        _bookNameProvider = new JsonBookNameProvider(BooksJsonUri);

        LoadLookupMetadata();

        try
        {
            IUsxBibleLoader loader = new UsxBibleAssetLoader(new UsxBibleParser());
            var book = loader.LoadFromAsset(SampleUsxUri);

            _bookCode = book.Code;
            _bookTitle = _bookNameProvider.GetEnglishName(book.Code);
            Paragraphs = book.Paragraphs;
            Status = $"Loaded {book.VerseCount} verses across {book.Paragraphs.Count} paragraphs from local USX asset.";

            // Sync lookup selection to loaded content (default jhn 1:1 in this sample).
            var initialBook = _lookupBooks.FirstOrDefault(b =>
                string.Equals(b.Code, _bookCode, StringComparison.OrdinalIgnoreCase));
            if (initialBook != null)
                SelectedLookupBook = initialBook;
            else if (_lookupBooks.Count > 0)
                SelectedLookupBook = _lookupBooks[0];

            SelectedLookupChapter = 1;
            SelectedLookupVerse = 1;
            Header = $"{_bookTitle} {SelectedLookupChapter}:{SelectedLookupVerse}";
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

    public IReadOnlyList<BibleParagraph> Paragraphs { get; }

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

            var chapterCount = GetChapterCount(value.Code);
            LookupChapters = Enumerable.Range(1, chapterCount).ToArray();

            if (_selectedLookupChapter < 1 || _selectedLookupChapter > chapterCount)
                SelectedLookupChapter = 1;
            else
                RefreshVerses();
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

            RefreshVerses();
        }
    }

    public int SelectedLookupVerse
    {
        get => _selectedLookupVerse;
        set => this.RaiseAndSetIfChanged(ref _selectedLookupVerse, value);
    }

    private void RefreshVerses()
    {
        var code = _selectedLookupBook?.Code;
        if (string.IsNullOrWhiteSpace(code))
        {
            LookupVerses = [];
            return;
        }

        var verseCount = GetVerseCount(code, _selectedLookupChapter);
        LookupVerses = Enumerable.Range(1, verseCount).ToArray();

        if (_selectedLookupVerse < 1 || _selectedLookupVerse > verseCount)
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
            var booksDocument = ReadJsonAsset(BooksJsonUri);
            var lastVerseDocument = ReadJsonAsset(LastVerseJsonUri);

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
            // Keep fallback values if metadata cannot be loaded.
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
}
