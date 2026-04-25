using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MyBibleApp.Services.Sync;
using Xunit;

namespace MyBibleApp.Sync.Tests;

public sealed class FileBasedLocalStorageProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"storage_test_{Guid.NewGuid():N}");
    private readonly FileBasedLocalStorageProvider _storage;

    public FileBasedLocalStorageProviderTests()
    {
        Directory.CreateDirectory(_tempDir);
        _storage = new FileBasedLocalStorageProvider(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task SaveAndGet_StringValue_RoundTrips()
    {
        await _storage.SaveAsync("greeting", "hello world");

        var result = await _storage.GetAsync("greeting");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task Get_NonExistentKey_ReturnsNull()
    {
        var result = await _storage.GetAsync("missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAndGetObject_RoundTrips()
    {
        var prefs = new PreferencesSnapshot
        {
            Theme = "Dark",
            FontSize = 22.0,
            ShowVerseNumbers = false,
            Language = "fr-FR"
        };

        await _storage.SaveObjectAsync("prefs", prefs);
        var loaded = await _storage.GetObjectAsync<PreferencesSnapshot>("prefs");

        Assert.NotNull(loaded);
        Assert.Equal("Dark", loaded!.Theme);
        Assert.Equal(22.0, loaded.FontSize);
        Assert.False(loaded.ShowVerseNumbers);
        Assert.Equal("fr-FR", loaded.Language);
    }

    [Fact]
    public async Task GetObject_NonExistentKey_ReturnsDefault()
    {
        var result = await _storage.GetObjectAsync<PreferencesSnapshot>("missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task ContainsKey_ExistingKey_ReturnsTrue()
    {
        await _storage.SaveAsync("key1", "value1");

        Assert.True(await _storage.ContainsKeyAsync("key1"));
    }

    [Fact]
    public async Task ContainsKey_MissingKey_ReturnsFalse()
    {
        Assert.False(await _storage.ContainsKeyAsync("nonexistent"));
    }

    [Fact]
    public async Task Remove_DeletesKey()
    {
        await _storage.SaveAsync("key1", "value1");
        await _storage.RemoveAsync("key1");

        Assert.False(await _storage.ContainsKeyAsync("key1"));
        Assert.Null(await _storage.GetAsync("key1"));
    }

    [Fact]
    public async Task Remove_NonExistentKey_DoesNotThrow()
    {
        await _storage.RemoveAsync("nonexistent");
    }

    [Fact]
    public async Task Clear_RemovesAllKeys()
    {
        await _storage.SaveAsync("key1", "value1");
        await _storage.SaveAsync("key2", "value2");
        await _storage.SaveAsync("key3", "value3");

        await _storage.ClearAsync();

        Assert.False(await _storage.ContainsKeyAsync("key1"));
        Assert.False(await _storage.ContainsKeyAsync("key2"));
        Assert.False(await _storage.ContainsKeyAsync("key3"));
    }

    [Fact]
    public async Task Save_OverwritesPreviousValue()
    {
        await _storage.SaveAsync("key", "first");
        await _storage.SaveAsync("key", "second");

        var result = await _storage.GetAsync("key");
        Assert.Equal("second", result);
    }

    [Fact]
    public async Task Save_ConcurrentInstancesSharingDirectory_DoNotCollide()
    {
        var first = new FileBasedLocalStorageProvider(_tempDir);
        var second = new FileBasedLocalStorageProvider(_tempDir);

        await Task.WhenAll(
            Enumerable.Range(0, 25).Select(index =>
                (index % 2 == 0 ? first : second).SaveAsync("shared", $"value-{index}")));

        var result = await first.GetAsync("shared");
        Assert.StartsWith("value-", result);
    }

    [Fact]
    public async Task SaveObject_ReadingProgressSnapshot_RoundTrips()
    {
        var progress = new ReadingProgressSnapshot
        {
            BookCode = "JHN",
            Chapter = 3,
            Verse = 16
        };

        await _storage.SaveObjectAsync("progress", progress);
        var loaded = await _storage.GetObjectAsync<ReadingProgressSnapshot>("progress");

        Assert.NotNull(loaded);
        Assert.Equal("JHN", loaded!.BookCode);
        Assert.Equal(3, loaded.Chapter);
        Assert.Equal(16, loaded.Verse);
    }

    [Fact]
    public async Task SaveObject_AnnotationBundle_RoundTrips()
    {
        var annotation = new AnnotationBundle
        {
            BookCode = "PSA",
            Chapter = 23,
            Verse = 1,
            Notes = "The Lord is my shepherd",
            HighlightColor = "#FFD700",
            IsBookmarked = true
        };

        await _storage.SaveObjectAsync("annotation", annotation);
        var loaded = await _storage.GetObjectAsync<AnnotationBundle>("annotation");

        Assert.NotNull(loaded);
        Assert.Equal("PSA", loaded!.BookCode);
        Assert.Equal("The Lord is my shepherd", loaded.Notes);
        Assert.Equal("#FFD700", loaded.HighlightColor);
        Assert.True(loaded.IsBookmarked);
    }
}

