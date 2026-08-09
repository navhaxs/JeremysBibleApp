using System;
using System.IO;
using System.Threading.Tasks;
using MyBibleApp.Services;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class UsxBibleZipLoaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"zip_loader_test_{Guid.NewGuid():N}");

    public UsxBibleZipLoaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private const string GenUsx = "<usx version=\"3.0\"><book code=\"GEN\" style=\"id\">Genesis</book><chapter number=\"1\" style=\"c\"/><para style=\"p\"><verse number=\"1\" style=\"v\"/>In the beginning.</para></usx>";

    [Fact]
    public async Task LoadBookAsync_FileExists_ParsesAndReturnsBook()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "gen.usx"), GenUsx);
        var loader = new UsxBibleZipLoader(_tempDir, new UsxBibleParser());

        var book = await loader.LoadBookAsync("gen");

        Assert.Equal("GEN", book.Code);
        Assert.True(book.VerseCount > 0);
    }

    [Fact]
    public async Task LoadBookAsync_CodeIsCaseInsensitiveAndTrimmed()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "gen.usx"), GenUsx);
        var loader = new UsxBibleZipLoader(_tempDir, new UsxBibleParser());

        var book = await loader.LoadBookAsync(" GEN ");

        Assert.Equal("GEN", book.Code);
    }

    [Fact]
    public async Task LoadBookAsync_MissingBook_ThrowsFileNotFoundException()
    {
        var loader = new UsxBibleZipLoader(_tempDir, new UsxBibleParser());

        await Assert.ThrowsAsync<FileNotFoundException>(() => loader.LoadBookAsync("rev"));
    }
}
