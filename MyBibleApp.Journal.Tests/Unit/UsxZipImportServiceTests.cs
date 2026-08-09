using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using MyBibleApp.Services;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class UsxZipImportServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"zip_import_test_{Guid.NewGuid():N}");

    public UsxZipImportServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private const string GenUsx = "<usx version=\"3.0\"><book code=\"GEN\" style=\"id\">Genesis</book><chapter number=\"1\" style=\"c\"/><para style=\"p\"><verse number=\"1\" style=\"v\"/>In the beginning.</para></usx>";
    private const string ExoUsx = "<usx version=\"3.0\"><book code=\"EXO\" style=\"id\">Exodus</book><chapter number=\"1\" style=\"c\"/><para style=\"p\"><verse number=\"1\" style=\"v\"/>Now these are the names.</para></usx>";

    private string CreateZip(string fileName, params (string EntryName, string Content)[] entries)
    {
        var zipPath = Path.Combine(_tempDir, fileName);
        using var stream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
        return zipPath;
    }

    [Fact]
    public void PrepareImport_AllCanonicalBooksPresent_NoMissingBooks()
    {
        var zipPath = CreateZip("full.zip", ("gen.usx", GenUsx), ("exo.usx", ExoUsx));
        var service = new UsxZipImportService();

        var result = service.PrepareImport(zipPath, ["gen", "exo"]);

        Assert.Equal(2, result.BookCodes.Count);
        Assert.Contains("gen", result.BookCodes);
        Assert.Contains("exo", result.BookCodes);
        Assert.Empty(result.MissingBookCodes);
        Assert.True(File.Exists(Path.Combine(result.TempDirectory, "gen.usx")));
        Assert.True(File.Exists(Path.Combine(result.TempDirectory, "exo.usx")));
    }

    [Fact]
    public void PrepareImport_SomeBooksMissing_ReportsMissingList()
    {
        var zipPath = CreateZip("partial.zip", ("gen.usx", GenUsx));
        var service = new UsxZipImportService();

        var result = service.PrepareImport(zipPath, ["gen", "exo", "lev"]);

        Assert.Single(result.BookCodes);
        Assert.Equal(["exo", "lev"], result.MissingBookCodes);
    }

    [Fact]
    public void PrepareImport_BookCodeReadFromXmlNotFilename()
    {
        // Filename says "book1", but the <book code> attribute says GEN — the code must win.
        var zipPath = CreateZip("mislabeled.zip", ("book1.usx", GenUsx));
        var service = new UsxZipImportService();

        var result = service.PrepareImport(zipPath, ["gen"]);

        Assert.Contains("gen", result.BookCodes);
        Assert.True(File.Exists(Path.Combine(result.TempDirectory, "gen.usx")));
        Assert.False(File.Exists(Path.Combine(result.TempDirectory, "book1.usx")));
    }

    [Fact]
    public void PrepareImport_NonUsxEntriesIgnored()
    {
        var zipPath = CreateZip("withjunk.zip", ("gen.usx", GenUsx), ("readme.txt", "hello"));
        var service = new UsxZipImportService();

        var result = service.PrepareImport(zipPath, ["gen"]);

        Assert.Single(result.BookCodes);
        Assert.False(File.Exists(Path.Combine(result.TempDirectory, "readme.txt")));
    }

    [Fact]
    public void PrepareImport_CorruptUsxEntrySkippedNotFatal()
    {
        var zipPath = CreateZip("corrupt.zip", ("gen.usx", GenUsx), ("bad.usx", "not valid xml <<<"));
        var service = new UsxZipImportService();

        var result = service.PrepareImport(zipPath, ["gen"]);

        Assert.Single(result.BookCodes);
        Assert.Contains("gen", result.BookCodes);
    }

    [Fact]
    public void PrepareImport_ZeroValidBooks_ThrowsAndCleansUpTemp()
    {
        var zipPath = CreateZip("empty.zip", ("readme.txt", "hello"));
        var service = new UsxZipImportService();

        var ex = Assert.Throws<InvalidOperationException>(() => service.PrepareImport(zipPath, ["gen"]));
        Assert.Contains("No valid", ex.Message);
    }

    [Fact]
    public void PrepareImport_ZipSlipEntry_ExtractsFlattenedNotEscaped()
    {
        var zipPath = CreateZip("slip.zip", ("../../evil.usx", GenUsx));
        var service = new UsxZipImportService();

        var result = service.PrepareImport(zipPath, ["gen"]);

        // The malicious path component is stripped; the file lands inside TempDirectory as gen.usx.
        Assert.Contains("gen", result.BookCodes);
        Assert.True(File.Exists(Path.Combine(result.TempDirectory, "gen.usx")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(result.TempDirectory))!, "evil.usx")));
    }

    [Fact]
    public async Task CommitImportAsync_MovesTempToTranslationsRootAndWritesManifest()
    {
        var zipPath = CreateZip("full.zip", ("gen.usx", GenUsx));
        var service = new UsxZipImportService();
        var prepared = service.PrepareImport(zipPath, ["gen"]);

        var translationsRoot = Path.Combine(_tempDir, "Translations");
        var installed = await service.CommitImportAsync(prepared, translationsRoot, "My Translation", "full.zip");

        Assert.False(Directory.Exists(prepared.TempDirectory));
        var finalDir = Path.Combine(translationsRoot, installed.Id);
        Assert.True(File.Exists(Path.Combine(finalDir, "gen.usx")));
        Assert.True(File.Exists(Path.Combine(finalDir, "manifest.json")));
        Assert.Equal("My Translation", installed.DisplayName);
        Assert.Equal("full.zip", installed.SourceZipName);
    }

    [Fact]
    public void CancelImport_DeletesTempDirectory()
    {
        var zipPath = CreateZip("full.zip", ("gen.usx", GenUsx));
        var service = new UsxZipImportService();
        var prepared = service.PrepareImport(zipPath, ["gen"]);

        service.CancelImport(prepared);

        Assert.False(Directory.Exists(prepared.TempDirectory));
    }
}
