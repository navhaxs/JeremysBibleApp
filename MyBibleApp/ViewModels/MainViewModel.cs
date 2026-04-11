using System;
using System.Collections.Generic;
using MyBibleApp.Models;
using MyBibleApp.Services;
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const string SampleUsxUri = "avares://MyBibleApp/Assets/usx/sample-jhn1.usx";
    private const string BooksJsonUri = "avares://MyBibleApp/Assets/books.json";
    
    private string _header = string.Empty;
    private string _bookTitle = string.Empty;
    private readonly IBookNameProvider _bookNameProvider;
    private string _status = string.Empty;
// #if DEBUG
//     private bool _isDebugMode = true;
// #else
    private bool _isDebugMode;
// #endif

    public MainViewModel()
    {
        _bookNameProvider = new JsonBookNameProvider(BooksJsonUri);
        
        try
        {
            IUsxBibleLoader loader = new UsxBibleAssetLoader(new UsxBibleParser());
            var book = loader.LoadFromAsset(SampleUsxUri);

            _bookTitle = _bookNameProvider.GetEnglishName(book.Code);
            Header = $"{_bookTitle} 1:1";
            Status = $"Loaded {book.VerseCount} verses across {book.Paragraphs.Count} paragraphs from local USX asset.";
            Paragraphs = book.Paragraphs;
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
}
