using System;
using System.Collections.Generic;
using OpenBibleApp.Models;
using OpenBibleApp.Services;

namespace OpenBibleApp.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const string SampleUsxUri = "avares://OpenBibleApp/Assets/usx/sample-3jn.usx";

    public MainViewModel()
    {
        try
        {
            IUsxBibleLoader loader = new UsxBibleAssetLoader(new UsxBibleParser());
            var book = loader.LoadFromAsset(SampleUsxUri);

            Header = $"{book.Title} ({book.Code})";
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

    public string Header { get; }

    public string Status { get; }

    public IReadOnlyList<BibleParagraph> Paragraphs { get; }
}