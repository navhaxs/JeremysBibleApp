using System;
using System.IO;
using System.Threading.Tasks;
using MyBibleApp.Services;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class UiPreferencesStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ui_prefs_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task LoadJournalPanAsync_NoFileYet_ReturnsZeros()
    {
        var store = new UiPreferencesStore(_tempDir);
        var (portraitX, landscapeX) = await store.LoadJournalPanAsync();
        Assert.Equal(0, portraitX);
        Assert.Equal(0, landscapeX);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsValues()
    {
        var store = new UiPreferencesStore(_tempDir);
        await store.SaveJournalPanAsync(123.5, 678.25);

        var reloaded = new UiPreferencesStore(_tempDir);
        var (portraitX, landscapeX) = await reloaded.LoadJournalPanAsync();

        Assert.Equal(123.5, portraitX);
        Assert.Equal(678.25, landscapeX);
    }

    [Fact]
    public async Task LoadJournalPanAsync_CorruptFile_ReturnsZerosInsteadOfThrowing()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "ui-prefs.json"), "{ not valid json");

        var store = new UiPreferencesStore(_tempDir);
        var (portraitX, landscapeX) = await store.LoadJournalPanAsync();

        Assert.Equal(0, portraitX);
        Assert.Equal(0, landscapeX);
    }
}
