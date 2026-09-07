using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

/// <summary>
/// Singleton service that owns the Bible content loaders (BSB online + any imported
/// translations) and manages background prefetching of BSB books on app startup.
/// </summary>
internal sealed class BibleContentService
{
    private const string BooksJsonUri = "avares://MyBibleApp/Assets/books.json";

    private static readonly Lazy<BibleContentService> SharedInstance =
        new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly UsxBibleApiLoader _apiLoader;
    private readonly ConcurrentDictionary<string, UsxBibleZipLoader> _zipLoaders = new();
    private readonly CancellationTokenSource _prefetchCts = new();

    private BibleContentService(UsxBibleApiLoader apiLoader)
    {
        _apiLoader = apiLoader;
    }

    public static BibleContentService Instance => SharedInstance.Value;

    public Task<BibleBook> LoadBookAsync(string bookCode, string translationId)
    {
        if (translationId == TranslationManager.BsbOnlineId)
            return _apiLoader.LoadFromApiAsync(bookCode);

        var loader = _zipLoaders.GetOrAdd(translationId, id =>
            new UsxBibleZipLoader(TranslationManager.Instance.GetTranslationFolder(id), new UsxBibleParser()));
        return loader.LoadBookAsync(bookCode);
    }

    /// <summary>
    /// Starts background prefetch of all BSB books. Safe to call multiple times —
    /// only the first call has any effect.
    /// </summary>
    public void StartPrefetch(IEnumerable<string> bookCodes) =>
        _ = _apiLoader.PrefetchAllBooksAsync(bookCodes, _prefetchCts.Token);

    private static BibleContentService Create()
    {
        var loader = new UsxBibleApiLoader(new UsxBibleParser());
        var service = new BibleContentService(loader);
        service.StartPrefetch(LoadBookCodesFromAsset());
        return service;
    }

    internal static IEnumerable<string> LoadBookCodesFromAsset()
    {
        try
        {
            var uri = new Uri(BooksJsonUri, UriKind.Absolute);
            using var stream = AssetLoader.Open(uri);
            using var reader = new System.IO.StreamReader(stream);
            var json = reader.ReadToEnd();
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("books_ordered", out var arr))
                return arr.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList()!;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BibleContentService] Failed to load book list: {ex.Message}");
        }

        return [];
    }
}
