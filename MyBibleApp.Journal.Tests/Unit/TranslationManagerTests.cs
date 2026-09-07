using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MyBibleApp.Models;
using MyBibleApp.Services;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class TranslationManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"translation_mgr_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveJournalTranslationId_EmptyOrNull_FallsBackToBsbOnline(string? stored)
    {
        Assert.Equal(TranslationManager.BsbOnlineId, TranslationManager.ResolveJournalTranslationId(stored));
    }

    [Fact]
    public void ResolveJournalTranslationId_NonEmpty_PassesThrough()
    {
        Assert.Equal("abc123", TranslationManager.ResolveJournalTranslationId("abc123"));
    }

    [Fact]
    public async Task GetInstalledTranslationsAsync_NoTranslationsYet_ReturnsEmpty()
    {
        var manager = new TranslationManager(localStorageProvider: null, translationsRoot: _tempDir);
        var result = await manager.GetInstalledTranslationsAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetInstalledTranslationsAsync_ReadsManifestsFromEachSubfolder()
    {
        var translationDir = Path.Combine(_tempDir, "t1");
        Directory.CreateDirectory(translationDir);
        var manifest = new InstalledTranslation
        {
            Id = "t1",
            DisplayName = "My ESV",
            SourceZipName = "esv.zip",
            ImportedAtUtc = DateTime.UtcNow,
            BookCodes = ["gen", "exo"],
            MissingBookCodes = ["rev"]
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        });
        await File.WriteAllTextAsync(Path.Combine(translationDir, "manifest.json"), json);

        var manager = new TranslationManager(localStorageProvider: null, translationsRoot: _tempDir);
        var result = await manager.GetInstalledTranslationsAsync();

        Assert.Single(result);
        Assert.Equal("My ESV", result[0].DisplayName);
        Assert.Equal(["gen", "exo"], result[0].BookCodes);
        Assert.Equal(["rev"], result[0].MissingBookCodes);
    }

    [Fact]
    public async Task GetInstalledTranslationsAsync_SkipsFolderWithoutManifest()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "corrupt-entry"));

        var manager = new TranslationManager(localStorageProvider: null, translationsRoot: _tempDir);
        var result = await manager.GetInstalledTranslationsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActiveTranslationIdAsync_NoStorageProvider_ReturnsBsbOnline()
    {
        var manager = new TranslationManager(localStorageProvider: null, translationsRoot: _tempDir);
        Assert.Equal(TranslationManager.BsbOnlineId, await manager.GetActiveTranslationIdAsync());
    }

    [Fact]
    public async Task SetThenGetActiveTranslationId_RoundTrips()
    {
        var store = new FakeLocalStorageProvider();
        var manager = new TranslationManager(store, _tempDir);

        await manager.SetActiveTranslationIdAsync("t1");
        Assert.Equal("t1", await manager.GetActiveTranslationIdAsync());
    }

    [Fact]
    public async Task DeleteTranslationAsync_RemovesFolder()
    {
        var translationDir = Path.Combine(_tempDir, "t1");
        Directory.CreateDirectory(translationDir);
        File.WriteAllText(Path.Combine(translationDir, "manifest.json"), "{}");

        var manager = new TranslationManager(localStorageProvider: null, translationsRoot: _tempDir);
        var result = await manager.DeleteTranslationAsync("t1");

        Assert.True(result.IsSuccess);
        Assert.False(Directory.Exists(translationDir));
    }

    [Fact]
    public async Task DeleteTranslationAsync_RefusesToDeleteBsbOnline()
    {
        var manager = new TranslationManager(localStorageProvider: null, translationsRoot: _tempDir);
        var result = await manager.DeleteTranslationAsync(TranslationManager.BsbOnlineId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RenameTranslationAsync_UpdatesManifestDisplayName()
    {
        var translationDir = Path.Combine(_tempDir, "t1");
        Directory.CreateDirectory(translationDir);
        var manifest = new InstalledTranslation { Id = "t1", DisplayName = "Old Name" };
        await File.WriteAllTextAsync(Path.Combine(translationDir, "manifest.json"), JsonSerializer.Serialize(manifest));

        var manager = new TranslationManager(localStorageProvider: null, translationsRoot: _tempDir);
        var result = await manager.RenameTranslationAsync("t1", "New Name");
        Assert.True(result.IsSuccess);

        var reloaded = await manager.GetInstalledTranslationsAsync();
        Assert.Equal("New Name", reloaded[0].DisplayName);
    }

    private sealed class FakeLocalStorageProvider : Services.Sync.ILocalStorageProvider
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _values = new();
        public Task SaveAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task SaveObjectAsync<T>(string key, T obj) { _values[key] = JsonSerializer.Serialize(obj); return Task.CompletedTask; }
        public Task<T?> GetObjectAsync<T>(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? JsonSerializer.Deserialize<T>(v) : default);
        public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
        public Task<bool> ContainsKeyAsync(string key) => Task.FromResult(_values.ContainsKey(key));
        public Task ClearAsync() { _values.Clear(); return Task.CompletedTask; }
    }
}
