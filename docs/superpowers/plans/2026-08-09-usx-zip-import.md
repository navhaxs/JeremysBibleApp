# USX ZIP Translation Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user import a ZIP of USX files as a custom Bible translation, switch between it and the existing online BSB source, and have Journals remember/restore which translation they were written against.

**Architecture:** Three new pure services (`TranslationManager`, `UsxZipImportService`, `UsxBibleZipLoader`) that mirror the existing `JournalStore`/`UiPreferencesStore` disk-persistence pattern (constructor takes an optional storage-root override for testability, defaults to a real `%APPDATA%` path in production). `BibleContentService` gains translation-aware routing. `AppShellView`'s journal load/create handlers read and write the active translation. A new "Translations" section is added to the existing Settings flyout in `MainView.axaml`.

**Tech Stack:** .NET 10, Avalonia, xUnit, `System.Text.Json`, `System.IO.Compression` (BCL, no new package needed).

## Global Constraints

- Target framework `net10.0`, nullable enabled — match every existing file in `MyBibleApp/`.
- JSON persistence uses this exact options instance (copy from `JournalStore.cs`/`UiPreferencesStore.cs`): `new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true }`.
- All disk writes use the existing atomic write pattern: write to `{path}.{Guid:N}.tmp`, then `File.Move(tempPath, finalPath, overwrite: true)`.
- Book codes are compared case-insensitively, normalized via `.Trim().ToLowerInvariant()` — matches `UsxBibleApiLoader`'s existing normalization.
- New services follow the `JournalStore(string? storagePath = null)` / `UiPreferencesStore(string? storagePath = null)` constructor convention: an optional override parameter for tests, defaulting to a real `%APPDATA%\MyBibleApp\...` path in production.
- Tests go in `MyBibleApp.Journal.Tests/Unit/` (the only test project referencing `MyBibleApp.csproj`), using xUnit `[Fact]`, one temp directory per test class cleaned up via `IDisposable` — copy the exact style of `MyBibleApp.Journal.Tests/Unit/UiPreferencesStoreTests.cs`.
- No new NuGet packages. No new dialog/modal library — confirmation UI is done with in-flyout visibility toggles (bound `bool` properties), matching how this app already has no `ContentDialog`/`MessageBox` usage anywhere.
- Zero automated tests exist today for `AppViewModel`, any `.axaml.cs` code-behind, or `BibleContentService` — this plan does not introduce new test coverage for those either (matches existing project convention); those tasks are verified manually by running the app.

---

### Task 1: `InstalledTranslation` model + `TranslationManager` service

**Files:**
- Create: `MyBibleApp/Models/InstalledTranslation.cs`
- Create: `MyBibleApp/Services/TranslationManager.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/TranslationManagerTests.cs`

**Interfaces:**
- Produces: `InstalledTranslation { string Id, string DisplayName, string SourceZipName, DateTime ImportedAtUtc, IReadOnlyList<string> BookCodes, IReadOnlyList<string> MissingBookCodes }`
- Produces: `TranslationManager.BsbOnlineId` (const `"bsb-online"`)
- Produces: `TranslationManager(ILocalStorageProvider? localStorageProvider, string? translationsRoot = null)`
- Produces: `Task<IReadOnlyList<InstalledTranslation>> GetInstalledTranslationsAsync()`
- Produces: `Task<string> GetActiveTranslationIdAsync()`
- Produces: `Task SetActiveTranslationIdAsync(string translationId)`
- Produces: `string GetTranslationFolder(string translationId)`
- Produces: `Task<Result> DeleteTranslationAsync(string translationId)`
- Produces: `Task<Result> RenameTranslationAsync(string translationId, string newDisplayName)`
- Produces: `static string ResolveJournalTranslationId(string? storedTranslationId)` — pure helper, empty/null → `BsbOnlineId`, else passthrough
- Produces: `static TranslationManager Instance` (production singleton, wired to `SharedSyncRuntime.Instance.LocalStorageProvider`)

- [ ] **Step 1: Write the model**

```csharp
// MyBibleApp/Models/InstalledTranslation.cs
using System;
using System.Collections.Generic;

namespace MyBibleApp.Models;

public sealed class InstalledTranslation
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SourceZipName { get; init; } = string.Empty;
    public DateTime ImportedAtUtc { get; init; }
    public IReadOnlyList<string> BookCodes { get; init; } = [];
    public IReadOnlyList<string> MissingBookCodes { get; init; } = [];
}
```

- [ ] **Step 2: Write failing tests for the pure helper and disk-backed behavior**

```csharp
// MyBibleApp.Journal.Tests/Unit/TranslationManagerTests.cs
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
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test MyBibleApp.Journal.Tests --filter TranslationManagerTests`
Expected: FAIL — `TranslationManager` and `InstalledTranslation` don't exist yet (compile error).

- [ ] **Step 4: Implement `TranslationManager`**

```csharp
// MyBibleApp/Services/TranslationManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MyBibleApp.Models;
using MyBibleApp.Services.Sync;

namespace MyBibleApp.Services;

public sealed class TranslationManager
{
    public const string BsbOnlineId = "bsb-online";
    private const string ActiveTranslationIdKey = "ActiveTranslationId";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<TranslationManager> SharedInstance =
        new(() => new TranslationManager(SharedSyncRuntime.Instance.LocalStorageProvider), LazyThreadSafetyMode.ExecutionAndPublication);

    public static TranslationManager Instance => SharedInstance.Value;

    private readonly ILocalStorageProvider? _localStorageProvider;
    private readonly string _translationsRoot;

    public TranslationManager(ILocalStorageProvider? localStorageProvider, string? translationsRoot = null)
    {
        _localStorageProvider = localStorageProvider;
        _translationsRoot = translationsRoot ?? GetDefaultTranslationsRoot();
    }

    public static string ResolveJournalTranslationId(string? storedTranslationId) =>
        string.IsNullOrWhiteSpace(storedTranslationId) ? BsbOnlineId : storedTranslationId;

    public string GetTranslationFolder(string translationId) => Path.Combine(_translationsRoot, translationId);

    public async Task<IReadOnlyList<InstalledTranslation>> GetInstalledTranslationsAsync()
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(_translationsRoot))
                return (IReadOnlyList<InstalledTranslation>)[];

            var results = new List<InstalledTranslation>();
            foreach (var dir in Directory.GetDirectories(_translationsRoot))
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<InstalledTranslation>(json, JsonOptions);
                    if (manifest != null) results.Add(manifest);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TranslationManager] Failed to read manifest at '{manifestPath}': {ex.Message}");
                }
            }

            return (IReadOnlyList<InstalledTranslation>)results.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }).ConfigureAwait(false);
    }

    public async Task<string> GetActiveTranslationIdAsync()
    {
        if (_localStorageProvider == null) return BsbOnlineId;
        try
        {
            var stored = await _localStorageProvider.GetAsync(ActiveTranslationIdKey).ConfigureAwait(false);
            return ResolveJournalTranslationId(stored);
        }
        catch
        {
            return BsbOnlineId;
        }
    }

    public async Task SetActiveTranslationIdAsync(string translationId)
    {
        if (_localStorageProvider == null) return;
        try
        {
            await _localStorageProvider.SaveAsync(ActiveTranslationIdKey, translationId).ConfigureAwait(false);
        }
        catch { /* best-effort, matches AppViewModel's persistence pattern */ }
    }

    public Task<Result> DeleteTranslationAsync(string translationId)
    {
        if (translationId == BsbOnlineId)
            return Task.FromResult(Result.Failure("Cannot delete the built-in BSB translation."));

        return Task.Run(() =>
        {
            try
            {
                var dir = GetTranslationFolder(translationId);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Failure($"Failed to delete translation: {ex.Message}");
            }
        });
    }

    public async Task<Result> RenameTranslationAsync(string translationId, string newDisplayName)
    {
        var manifestPath = Path.Combine(GetTranslationFolder(translationId), "manifest.json");
        if (!File.Exists(manifestPath))
            return Result.Failure("Translation not found.");

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<InstalledTranslation>(json, JsonOptions);
            if (manifest == null)
                return Result.Failure("Translation manifest is corrupt.");

            var updated = new InstalledTranslation
            {
                Id = manifest.Id,
                DisplayName = newDisplayName,
                SourceZipName = manifest.SourceZipName,
                ImportedAtUtc = manifest.ImportedAtUtc,
                BookCodes = manifest.BookCodes,
                MissingBookCodes = manifest.MissingBookCodes
            };

            WriteAtomically(manifestPath, JsonSerializer.Serialize(updated, JsonOptions));
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to rename translation: {ex.Message}");
        }
    }

    private static void WriteAtomically(string filePath, string content)
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);
    }

    private static string GetDefaultTranslationsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MyBibleApp", "Translations");
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test MyBibleApp.Journal.Tests --filter TranslationManagerTests`
Expected: PASS (all cases)

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Models/InstalledTranslation.cs MyBibleApp/Services/TranslationManager.cs MyBibleApp.Journal.Tests/Unit/TranslationManagerTests.cs
git commit -m "feat: add TranslationManager for installed-translation tracking and switching"
```

---

### Task 2: `UsxZipImportService` (validate + extract ZIP, zip-slip and size guards)

**Files:**
- Create: `MyBibleApp/Models/PreparedTranslationImport.cs`
- Create: `MyBibleApp/Services/UsxZipImportService.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/UsxZipImportServiceTests.cs`

**Interfaces:**
- Consumes: `UsxBibleParser.Parse(XDocument) : BibleBook` (existing, unchanged) — used to read `<book code>` from each extracted file.
- Produces: `PreparedTranslationImport { string TempDirectory, IReadOnlyList<string> BookCodes, IReadOnlyList<string> MissingBookCodes }`
- Produces: `UsxZipImportService(UsxBibleParser? parser = null)`
- Produces: `PreparedTranslationImport PrepareImport(string zipFilePath, IReadOnlyList<string> canonicalBookCodes)` — throws `InvalidOperationException` if the ZIP has zero valid `.usx` books, or exceeds the size cap; cleans up its temp directory before throwing.
- Produces: `Task<InstalledTranslation> CommitImportAsync(PreparedTranslationImport prepared, string translationsRoot, string displayName, string sourceZipName)`
- Produces: `void CancelImport(PreparedTranslationImport prepared)`

- [ ] **Step 1: Write the model**

```csharp
// MyBibleApp/Models/PreparedTranslationImport.cs
using System.Collections.Generic;

namespace MyBibleApp.Models;

public sealed class PreparedTranslationImport
{
    public required string TempDirectory { get; init; }
    public required IReadOnlyList<string> BookCodes { get; init; }
    public required IReadOnlyList<string> MissingBookCodes { get; init; }
}
```

- [ ] **Step 2: Write failing tests**

Build test ZIPs in-memory using `System.IO.Compression.ZipArchive` in `ZipArchiveMode.Create` over a `MemoryStream`, then write to a temp `.zip` file, so each test is self-contained (no checked-in fixture files).

```csharp
// MyBibleApp.Journal.Tests/Unit/UsxZipImportServiceTests.cs
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
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test MyBibleApp.Journal.Tests --filter UsxZipImportServiceTests`
Expected: FAIL — `UsxZipImportService` doesn't exist yet.

- [ ] **Step 4: Implement `UsxZipImportService`**

```csharp
// MyBibleApp/Services/UsxZipImportService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class UsxZipImportService
{
    private const long MaxUncompressedBytes = 200L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly UsxBibleParser _parser;

    public UsxZipImportService(UsxBibleParser? parser = null)
    {
        _parser = parser ?? new UsxBibleParser();
    }

    public PreparedTranslationImport PrepareImport(string zipFilePath, IReadOnlyList<string> canonicalBookCodes)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "MyBibleAppImport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var discoveredCodes = ExtractAndDiscoverBookCodes(zipFilePath, tempDir);

            if (discoveredCodes.Count == 0)
                throw new InvalidOperationException("No valid USX books were found in the ZIP.");

            var missing = canonicalBookCodes
                .Select(c => c.Trim().ToLowerInvariant())
                .Where(c => !discoveredCodes.Contains(c))
                .ToList();

            return new PreparedTranslationImport
            {
                TempDirectory = tempDir,
                BookCodes = discoveredCodes.ToList(),
                MissingBookCodes = missing
            };
        }
        catch
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            throw;
        }
    }

    private HashSet<string> ExtractAndDiscoverBookCodes(string zipFilePath, string tempDir)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressed = 0;
        var normalizedTempDir = Path.GetFullPath(tempDir);

        using var archive = ZipFile.OpenRead(zipFilePath);
        foreach (var entry in archive.Entries)
        {
            var flatName = Path.GetFileName(entry.FullName);
            if (string.IsNullOrEmpty(flatName)) continue; // directory entry
            if (!flatName.EndsWith(".usx", StringComparison.OrdinalIgnoreCase)) continue;

            totalUncompressed += entry.Length;
            if (totalUncompressed > MaxUncompressedBytes)
                throw new InvalidOperationException($"ZIP exceeds the {MaxUncompressedBytes / (1024 * 1024)}MB uncompressed size limit.");

            var destinationPath = Path.GetFullPath(Path.Combine(tempDir, flatName));
            if (!destinationPath.StartsWith(normalizedTempDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue; // zip-slip guard

            try
            {
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UsxZipImportService] Failed to extract '{entry.FullName}': {ex.Message}");
                continue;
            }

            string? code = null;
            try
            {
                var doc = XDocument.Load(destinationPath, LoadOptions.PreserveWhitespace);
                code = _parser.Parse(doc).Code;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UsxZipImportService] Failed to parse '{flatName}': {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(code))
                continue;

            var normalizedCode = code.Trim().ToLowerInvariant();
            var finalPath = Path.Combine(tempDir, $"{normalizedCode}.usx");
            if (!string.Equals(destinationPath, finalPath, StringComparison.OrdinalIgnoreCase))
                File.Move(destinationPath, finalPath, overwrite: true);

            discovered.Add(normalizedCode);
        }

        return discovered;
    }

    public async Task<InstalledTranslation> CommitImportAsync(PreparedTranslationImport prepared, string translationsRoot, string displayName, string sourceZipName)
    {
        var translationId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(translationsRoot);
        var finalDir = Path.Combine(translationsRoot, translationId);
        Directory.Move(prepared.TempDirectory, finalDir);

        var manifest = new InstalledTranslation
        {
            Id = translationId,
            DisplayName = displayName,
            SourceZipName = sourceZipName,
            ImportedAtUtc = DateTime.UtcNow,
            BookCodes = prepared.BookCodes,
            MissingBookCodes = prepared.MissingBookCodes
        };

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(finalDir, "manifest.json"), json).ConfigureAwait(false);

        return manifest;
    }

    public void CancelImport(PreparedTranslationImport prepared)
    {
        if (Directory.Exists(prepared.TempDirectory))
            Directory.Delete(prepared.TempDirectory, recursive: true);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test MyBibleApp.Journal.Tests --filter UsxZipImportServiceTests`
Expected: PASS (all cases, including the zip-slip and size-cap guards)

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Models/PreparedTranslationImport.cs MyBibleApp/Services/UsxZipImportService.cs MyBibleApp.Journal.Tests/Unit/UsxZipImportServiceTests.cs
git commit -m "feat: add UsxZipImportService with zip-slip and size-cap guards"
```

---

### Task 3: `UsxBibleZipLoader`

**Files:**
- Create: `MyBibleApp/Services/UsxBibleZipLoader.cs`
- Test: `MyBibleApp.Journal.Tests/Unit/UsxBibleZipLoaderTests.cs`

**Interfaces:**
- Consumes: `UsxBibleParser.Parse(XDocument) : BibleBook` (existing, unchanged)
- Produces: `UsxBibleZipLoader(string translationFolder, UsxBibleParser parser)`
- Produces: `Task<BibleBook> LoadBookAsync(string bookCode)` — throws `FileNotFoundException` if the book isn't present in this translation's folder.

- [ ] **Step 1: Write failing tests**

```csharp
// MyBibleApp.Journal.Tests/Unit/UsxBibleZipLoaderTests.cs
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MyBibleApp.Journal.Tests --filter UsxBibleZipLoaderTests`
Expected: FAIL — `UsxBibleZipLoader` doesn't exist yet.

- [ ] **Step 3: Implement `UsxBibleZipLoader`**

```csharp
// MyBibleApp/Services/UsxBibleZipLoader.cs
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class UsxBibleZipLoader
{
    private readonly string _translationFolder;
    private readonly UsxBibleParser _parser;

    public UsxBibleZipLoader(string translationFolder, UsxBibleParser parser)
    {
        _translationFolder = translationFolder;
        _parser = parser;
    }

    public async Task<BibleBook> LoadBookAsync(string bookCode)
    {
        var normalizedCode = bookCode.Trim().ToLowerInvariant();
        var path = Path.Combine(_translationFolder, $"{normalizedCode}.usx");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Book '{normalizedCode}' is not available in this translation.", path);

        var xml = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return _parser.Parse(document);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MyBibleApp.Journal.Tests --filter UsxBibleZipLoaderTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Services/UsxBibleZipLoader.cs MyBibleApp.Journal.Tests/Unit/UsxBibleZipLoaderTests.cs
git commit -m "feat: add UsxBibleZipLoader for lazy per-book loading from an imported translation"
```

---

### Task 4: Wire `BibleContentService` + `ScriptureViewModel` to route by active translation

**Files:**
- Modify: `MyBibleApp/Services/BibleContentService.cs`
- Modify: `MyBibleApp/ViewModels/ScriptureViewModel.cs:257-277` (`TryLoadBookFromApiAsync`)

**Interfaces:**
- Consumes: `TranslationManager.Instance.GetActiveTranslationIdAsync() : Task<string>` (Task 1), `TranslationManager.BsbOnlineId` (Task 1), `TranslationManager.Instance.GetTranslationFolder(string) : string` (Task 1), `UsxBibleZipLoader(string, UsxBibleParser)` (Task 3)
- Produces: `BibleContentService.LoadBookAsync(string bookCode, string translationId) : Task<BibleBook>` — replaces the old single-arg `LoadBookAsync(string bookCode)`. This is the only caller (`ScriptureViewModel.cs:266`), confirmed by repo-wide grep, so no back-compat overload is needed.

- [ ] **Step 1: Add a public static book-code accessor to `BibleContentService`**

The canonical 66-book code list already lives behind `BibleContentService`'s private `LoadBookCodesFromAsset()`. `UsxZipImportService.PrepareImport` (Task 2) needs this same list when building an import's `canonicalBookCodes` argument — expose it instead of duplicating the JSON-reading logic.

In `MyBibleApp/Services/BibleContentService.cs`, change:

```csharp
    private static IEnumerable<string> LoadBookCodesFromAsset()
```

to:

```csharp
    internal static IEnumerable<string> LoadBookCodesFromAsset()
```

(No other change to that method's body — it's already exactly what's needed.)

- [ ] **Step 2: Add translation-routing to `BibleContentService`**

Replace the whole file with:

```csharp
// MyBibleApp/Services/BibleContentService.cs
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
```

- [ ] **Step 3: Update `ScriptureViewModel.TryLoadBookFromApiAsync` to resolve the active translation**

In `MyBibleApp/ViewModels/ScriptureViewModel.cs`, change:

```csharp
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
```

to:

```csharp
    public async Task<(bool Success, string? Error)> TryLoadBookFromApiAsync(string bookCode, int chapter, int verse)
    {
        // Cancel the background sample load so it can't overwrite real content.
        _sampleLoadCts?.Cancel();
        _sampleLoadCts?.Dispose();
        _sampleLoadCts = null;

        try
        {
            var translationId = await TranslationManager.Instance.GetActiveTranslationIdAsync().ConfigureAwait(false);
            var book = await _bibleContent.LoadBookAsync(bookCode, translationId).ConfigureAwait(false);

            var sourceStatus = translationId == TranslationManager.BsbOnlineId
                ? "Loaded from fetch.bible API."
                : "Loaded from imported translation.";

            await Dispatcher.UIThread.InvokeAsync(() =>
                ApplyLoadedBook(book, sourceStatus, chapter, verse));

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
```

- [ ] **Step 4: Build the project**

Run: `dotnet build MyBibleApp/MyBibleApp.csproj`
Expected: Build succeeds. There is no automated test coverage for `BibleContentService` today (network/disk singleton, matches existing project convention of leaving it untested) — this task is verified in Task 6's manual run-through instead.

- [ ] **Step 5: Commit**

```bash
git add MyBibleApp/Services/BibleContentService.cs MyBibleApp/ViewModels/ScriptureViewModel.cs
git commit -m "feat: route book loading through the active translation"
```

---

### Task 5: Journal load/create coupling to the active translation

**Files:**
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs:967-990` (`OnJournalActivated`)
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs:1007-1040` (`OnSaveAsJournalRequested`)

**Interfaces:**
- Consumes: `TranslationManager.Instance.SetActiveTranslationIdAsync(string) : Task`, `TranslationManager.Instance.GetActiveTranslationIdAsync() : Task<string>`, `TranslationManager.ResolveJournalTranslationId(string?) : string` (all Task 1)
- Consumes: `ScriptureViewModel.TryLoadBookFromApiAsync` (Task 4) — already re-reads the active translation each call, so switching the active translation before calling it is sufficient; no new parameter needed on that method.

The pure fallback logic (`ResolveJournalTranslationId`) is already unit-tested in Task 1. This task is UI-glue code in a `.axaml.cs` file with zero existing test coverage anywhere in the codebase for that file — verified manually (see Step 3).

- [ ] **Step 1: Switch translation before rendering a re-opened journal**

In `MyBibleApp/Views/AppShellView.axaml.cs`, inside `OnJournalActivated` (currently at line 967), the journal is fetched at line 975 (`var journal = await SharedSyncRuntime.Instance.JournalStore.GetJournalAsync(journalId);`). Insert a translation switch immediately after the null-check and before `ReloadWindowedInkStrokesAsync()`:

```csharp
    private async void OnJournalActivated(object? sender, string journalId)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var vm = _tabs[_activeTabIndex];

        _tabActiveJournalIds[vm] = journalId;
        _tabEphemeralStrokes[vm].Clear();

        var journal = await SharedSyncRuntime.Instance.JournalStore.GetJournalAsync(journalId);
        if (journal == null) return;

        // Re-verify vm is still the active tab after async gap
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count || _tabs[_activeTabIndex] != vm) return;

        var journalTranslationId = TranslationManager.ResolveJournalTranslationId(journal.TranslationId);
        if (await TranslationManager.Instance.GetActiveTranslationIdAsync() != journalTranslationId)
        {
            await TranslationManager.Instance.SetActiveTranslationIdAsync(journalTranslationId);
            await vm.TryLoadBookFromApiAsync(journal.BookCode, journal.StartChapter, journal.StartVerse);
        }

        await ReloadWindowedInkStrokesAsync();
        _primaryView?.SetActiveJournalName(journal.Name);
        _primaryView?.SetUnsavedBadgeVisible(false);
        _primaryView?.SetJournalLayout(journal.Layout);

        CloseJournalFlyout();
        RequestPersistOpenTabReferences();
    }
```

(The duplicated "Re-verify vm is still the active tab" comment/check already present in the file at lines 978-982 stays as-is — this step only adds the new block after it, it does not remove the existing duplicate check.)

- [ ] **Step 2: Stamp the active translation when creating a journal**

In the same file, inside `OnSaveAsJournalRequested` (currently at line 1007), change:

```csharp
        var request = new JournalCreateRequest
        {
            Name = name,
            TranslationId = "",
            TranslationVersionDate = "",
```

to:

```csharp
        var activeTranslationId = await TranslationManager.Instance.GetActiveTranslationIdAsync();
        var request = new JournalCreateRequest
        {
            Name = name,
            TranslationId = activeTranslationId,
            TranslationVersionDate = "",
```

Place the `var activeTranslationId = ...` line right before the `var request = new JournalCreateRequest` statement (after the existing `var name = $"Journal {DateTime.Now:MMM d, h:mm tt}";` line).

- [ ] **Step 3: Manual verification**

Run the app (`dotnet run --project MyBibleApp.Desktop`) and:
1. With BSB active, create a journal on Genesis 1 — confirm it saves without error.
2. (After Task 6 lands) import a second translation, switch to it, open the journal created in step 1 — confirm the app switches back to BSB automatically before rendering (since that journal's `TranslationId` is now populated with BSB's id, not empty — this app has never shipped with a second translation yet, so there are no pre-existing journals with `TranslationId == ""` to test the empty-string fallback against; that fallback is covered by the `ResolveJournalTranslationId` unit tests in Task 1).
3. Create a new journal while the imported translation is active — confirm `journals.json` (`%APPDATA%\MyBibleApp\LocalStorage\journals.json`) shows the imported translation's id in that journal's `translationId` field, not an empty string.

- [ ] **Step 4: Commit**

```bash
git add MyBibleApp/Views/AppShellView.axaml.cs
git commit -m "feat: switch active translation on journal open, stamp it on journal create"
```

---

### Task 6: Settings UI — install, switch, rename, delete translations

**Files:**
- Create: `MyBibleApp/ViewModels/TranslationListItem.cs`
- Modify: `MyBibleApp/ViewModels/AppViewModel.cs`
- Modify: `MyBibleApp/Services/TranslationManager.cs` (add `GetTranslationsRootForCommit`)
- Modify: `MyBibleApp/Views/MainView.axaml` (Settings flyout, after the existing Theme section around line 267)
- Modify: `MyBibleApp/Views/MainView.axaml.cs`
- Modify: `MyBibleApp/Views/AppShellView.axaml.cs:607-617` (`RestoreTabsAndAuthAsync`)

**Interfaces:**
- Consumes: `TranslationManager.Instance` (Task 1) — `GetInstalledTranslationsAsync`, `GetActiveTranslationIdAsync`, `SetActiveTranslationIdAsync`, `DeleteTranslationAsync`, `RenameTranslationAsync`
- Consumes: `UsxZipImportService` (Task 2) — `PrepareImport`, `CommitImportAsync`, `CancelImport`
- Consumes: `BibleContentService.LoadBookCodesFromAsset()` (Task 4, now `internal`) — canonical book list for `PrepareImport`'s `canonicalBookCodes` argument
- Produces: `TranslationListItem` (new small view-model wrapper — see Step 1) with `InstalledTranslation Model`, `string Id`, `string DisplayName`, `bool IsRenaming { get; set; }`, `string PendingName { get; set; }`
- Produces (on `AppViewModel`): `ObservableCollection<TranslationListItem> InstalledTranslations`, `string ActiveTranslationId { get; set; }`, `bool HasPendingImportWarning { get; }`, `IReadOnlyList<string> PendingImportMissingBooks { get; }`, `Task LoadTranslationsFromStorageAsync()`, `Task RefreshTranslationsAsync()`, `Task<Result> PrepareTranslationImportAsync(string zipFilePath, string sourceZipName, string displayName)`, `Task ConfirmPendingImportAsync()`, `void CancelPendingImport()`, `Task<Result> DeleteTranslationAsync(string translationId)`, `Task<Result> RenameTranslationAsync(string translationId, string newName)`

No automated tests for this task — `AppViewModel` and `.axaml`/`.axaml.cs` have zero existing test coverage anywhere in this codebase (confirmed by repo-wide search), so this stays consistent with existing convention. Verified manually in Step 5.

- [ ] **Step 1: Add translation state and methods to `AppViewModel`**

Add the small row wrapper (its own file, keeping `AppViewModel.cs` focused on orchestration rather than per-row UI state):

```csharp
// MyBibleApp/ViewModels/TranslationListItem.cs
using MyBibleApp.Models;
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public sealed class TranslationListItem : ReactiveObject
{
    private bool _isRenaming;
    private string _pendingName;

    public TranslationListItem(InstalledTranslation model)
    {
        Model = model;
        _pendingName = model.DisplayName;
    }

    public InstalledTranslation Model { get; }
    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            this.RaiseAndSetIfChanged(ref _isRenaming, value);
            this.RaisePropertyChanged(nameof(IsNotRenaming));
        }
    }

    public bool IsNotRenaming => !_isRenaming;

    public string PendingName
    {
        get => _pendingName;
        set => this.RaiseAndSetIfChanged(ref _pendingName, value);
    }
}
```

Add these `using` statements at the top of `MyBibleApp/ViewModels/AppViewModel.cs` (alongside the existing ones):

```csharp
using MyBibleApp.Models;
```

(`MyBibleApp.Services` is already imported.)

Add these fields near the existing `_isDebugMode`/`_isTabBarVisible` fields:

```csharp
    private readonly TranslationManager _translationManager = TranslationManager.Instance;
    private readonly UsxZipImportService _importService = new();
    private readonly ObservableCollection<TranslationListItem> _installedTranslations = [];
    private string _activeTranslationId = TranslationManager.BsbOnlineId;
    private PreparedTranslationImport? _pendingImport;
    private string _pendingImportDisplayName = string.Empty;
    private string _pendingImportSourceZipName = string.Empty;
```

Add this new section after the existing `// ── Theme ──` section (after `LoadThemeFromStorageAsync`, before `// ── Sync Status ──`):

```csharp
    // ── Translations ─────────────────────────────────────────────────────────

    public ObservableCollection<TranslationListItem> InstalledTranslations => _installedTranslations;

    public string ActiveTranslationId
    {
        get => _activeTranslationId;
        set
        {
            if (_activeTranslationId == value) return;
            this.RaiseAndSetIfChanged(ref _activeTranslationId, value);
            _ = _translationManager.SetActiveTranslationIdAsync(value);
        }
    }

    public bool HasPendingImportWarning => _pendingImport != null && _pendingImport.MissingBookCodes.Count > 0;

    public IReadOnlyList<string> PendingImportMissingBooks => _pendingImport?.MissingBookCodes ?? [];

    public async Task LoadTranslationsFromStorageAsync()
    {
        _activeTranslationId = await _translationManager.GetActiveTranslationIdAsync();
        this.RaisePropertyChanged(nameof(ActiveTranslationId));
        await RefreshTranslationsAsync();
    }

    public async Task RefreshTranslationsAsync()
    {
        var installed = await _translationManager.GetInstalledTranslationsAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _installedTranslations.Clear();
            foreach (var t in installed) _installedTranslations.Add(new TranslationListItem(t));
        });
    }

    public Task<Result> PrepareTranslationImportAsync(string zipFilePath, string sourceZipName, string displayName)
    {
        try
        {
            var canonicalCodes = BibleContentService.LoadBookCodesFromAsset().ToList();
            var prepared = _importService.PrepareImport(zipFilePath, canonicalCodes);

            _pendingImport = prepared;
            _pendingImportDisplayName = displayName;
            _pendingImportSourceZipName = sourceZipName;
            this.RaisePropertyChanged(nameof(HasPendingImportWarning));
            this.RaisePropertyChanged(nameof(PendingImportMissingBooks));

            if (prepared.MissingBookCodes.Count == 0)
                return ConfirmPendingImportInternalAsync();

            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure($"Import failed: {ex.Message}"));
        }
    }

    public Task ConfirmPendingImportAsync() => ConfirmPendingImportInternalAsync();

    private async Task<Result> ConfirmPendingImportInternalAsync()
    {
        if (_pendingImport == null) return Result.Failure("No pending import.");

        try
        {
            await _importService.CommitImportAsync(_pendingImport, _translationManager.GetTranslationsRootForCommit(), _pendingImportDisplayName, _pendingImportSourceZipName);
            ClearPendingImport();
            await RefreshTranslationsAsync();
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to finish import: {ex.Message}");
        }
    }

    public void CancelPendingImport()
    {
        if (_pendingImport == null) return;
        _importService.CancelImport(_pendingImport);
        ClearPendingImport();
    }

    private void ClearPendingImport()
    {
        _pendingImport = null;
        _pendingImportDisplayName = string.Empty;
        _pendingImportSourceZipName = string.Empty;
        this.RaisePropertyChanged(nameof(HasPendingImportWarning));
        this.RaisePropertyChanged(nameof(PendingImportMissingBooks));
    }

    public async Task<Result> DeleteTranslationAsync(string translationId)
    {
        var result = await _translationManager.DeleteTranslationAsync(translationId);
        if (result.IsSuccess)
        {
            if (ActiveTranslationId == translationId)
                ActiveTranslationId = TranslationManager.BsbOnlineId;
            await RefreshTranslationsAsync();
        }
        return result;
    }

    public async Task<Result> RenameTranslationAsync(string translationId, string newName)
    {
        var result = await _translationManager.RenameTranslationAsync(translationId, newName);
        if (result.IsSuccess)
            await RefreshTranslationsAsync();
        return result;
    }
```

`TranslationManager` needs one more small member to support `ConfirmPendingImportInternalAsync`'s commit call — add this public property to `MyBibleApp/Services/TranslationManager.cs` (Task 1's file) alongside `GetTranslationFolder`:

```csharp
    public string GetTranslationsRootForCommit() => _translationsRoot;
```

- [ ] **Step 2: Call `LoadTranslationsFromStorageAsync` on startup**

In `MyBibleApp/Views/AppShellView.axaml.cs`, inside `RestoreTabsAndAuthAsync` (currently at line 607), change:

```csharp
    private async Task RestoreTabsAndAuthAsync()
    {
        // Load persisted debug mode state early so the overlay is visible during restore.
        await _appVM.LoadDebugModeFromStorageAsync();
        await _appVM.LoadTabBarVisibleFromStorageAsync();

        // Load persisted theme and apply it.
        await _appVM.LoadThemeFromStorageAsync();
        var theme = Models.AppTheme.GetById(_appVM.SelectedThemeId);
        _primaryView?.ApplyTheme(theme);
```

to:

```csharp
    private async Task RestoreTabsAndAuthAsync()
    {
        // Load persisted debug mode state early so the overlay is visible during restore.
        await _appVM.LoadDebugModeFromStorageAsync();
        await _appVM.LoadTabBarVisibleFromStorageAsync();

        // Load persisted theme and apply it.
        await _appVM.LoadThemeFromStorageAsync();
        var theme = Models.AppTheme.GetById(_appVM.SelectedThemeId);
        _primaryView?.ApplyTheme(theme);

        // Load installed translations and the active selection before any book load below.
        await _appVM.LoadTranslationsFromStorageAsync();
```

- [ ] **Step 3: Add the Translations section to the Settings flyout**

In `MyBibleApp/Views/MainView.axaml`, after the existing Theme section (after the `ThemeSwatchPanel` `StackPanel` that ends around line 267, before the `IsDebugMode` `ToggleSwitch` at line 268), insert:

```xml
                                    <Rectangle
                                        Fill="{DynamicResource SystemControlForegroundBaseMediumLowBrush}"
                                        Height="1"
                                        Opacity="0.4" />
                                    <TextBlock
                                        FontSize="13"
                                        Margin="0,0,0,4"
                                        Opacity="0.7"
                                        Text="Translation" />
                                    <ItemsControl ItemsSource="{Binding AppVM.InstalledTranslations}">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate>
                                                <Panel>
                                                    <Grid ColumnDefinitions="*,Auto,Auto" IsVisible="{Binding IsNotRenaming}">
                                                        <RadioButton
                                                            Content="{Binding DisplayName}"
                                                            Grid.Column="0"
                                                            GroupName="TranslationSelector"
                                                            Click="OnTranslationRadioClick"
                                                            Tag="{Binding Id}" />
                                                        <Button
                                                            Click="OnRenameTranslationClick"
                                                            Content="Rename"
                                                            FontSize="11"
                                                            Grid.Column="1"
                                                            Padding="6,2"
                                                            Tag="{Binding}" />
                                                        <Button
                                                            Click="OnDeleteTranslationClick"
                                                            Content="Delete"
                                                            FontSize="11"
                                                            Grid.Column="2"
                                                            Padding="6,2"
                                                            Tag="{Binding Id}" />
                                                    </Grid>
                                                    <Grid ColumnDefinitions="*,Auto,Auto" IsVisible="{Binding IsRenaming}">
                                                        <TextBox
                                                            Grid.Column="0"
                                                            Text="{Binding PendingName, Mode=TwoWay}" />
                                                        <Button
                                                            Click="OnSaveRenameClick"
                                                            Content="Save"
                                                            FontSize="11"
                                                            Grid.Column="1"
                                                            Padding="6,2"
                                                            Tag="{Binding}" />
                                                        <Button
                                                            Click="OnCancelRenameClick"
                                                            Content="Cancel"
                                                            FontSize="11"
                                                            Grid.Column="2"
                                                            Padding="6,2"
                                                            Tag="{Binding}" />
                                                    </Grid>
                                                </Panel>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                    <RadioButton
                                        Content="BSB (online)"
                                        GroupName="TranslationSelector"
                                        Click="OnTranslationRadioClick"
                                        IsChecked="True"
                                        Tag="bsb-online" />
                                    <Button
                                        Click="OnImportTranslationClick"
                                        Content="Import Translation ZIP…"
                                        HorizontalAlignment="Stretch" />
                                    <Border
                                        Background="{DynamicResource SystemControlBackgroundBaseLowBrush}"
                                        CornerRadius="6"
                                        IsVisible="{Binding AppVM.HasPendingImportWarning}"
                                        Padding="8">
                                        <StackPanel Spacing="6">
                                            <TextBlock
                                                FontSize="12"
                                                Text="Some books are missing from this ZIP. Import anyway?"
                                                TextWrapping="Wrap" />
                                            <StackPanel Orientation="Horizontal" Spacing="6">
                                                <Button Click="OnConfirmImportClick" Content="Import Anyway" Padding="8,4" />
                                                <Button Click="OnCancelImportClick" Content="Cancel" Padding="8,4" />
                                            </StackPanel>
                                        </StackPanel>
                                    </Border>
```

Note: the built-in "BSB (online)" `RadioButton` is written as a static XAML entry (not part of `InstalledTranslations`, since `TranslationManager.GetInstalledTranslationsAsync` only scans imported translations on disk — BSB isn't a folder there). `IsChecked="True"` is its XAML-authored default; `OnTranslationRadioClick` (Step 4) will keep the actually-checked button in sync with `AppVM.ActiveTranslationId` on load.

- [ ] **Step 4: Add code-behind handlers to `MainView.axaml.cs`**

Add these `using` statements if not already present:

```csharp
using System.IO;
using MyBibleApp.Models;
using MyBibleApp.ViewModels;
```

Add these handlers near `OnThemeSwatchClick` (in the "Settings flyout handlers" region):

```csharp
    private void OnTranslationRadioClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string translationId }) return;
        if (DataContext is not ScriptureViewModel vm) return;

        vm.AppVM.ActiveTranslationId = translationId;
        _ = vm.TryLoadBookFromApiAsync(vm.BookCode, vm.SelectedLookupChapter, vm.SelectedLookupVerse);
    }

    private async void OnImportTranslationClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptureViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storageProvider) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Translation ZIP",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] }]
        });

        if (files.Count == 0) return;

        var file = files[0];
        var displayName = Path.GetFileNameWithoutExtension(file.Name);

        await using var stream = await file.OpenReadAsync();
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"import_{Guid.NewGuid():N}.zip");
        await using (var fileStream = File.Create(tempZipPath))
            await stream.CopyToAsync(fileStream);

        try
        {
            var result = await vm.AppVM.PrepareTranslationImportAsync(tempZipPath, file.Name, displayName);
            if (!result.IsSuccess)
                vm.Status = $"Import failed: {result.ErrorMessage}";
            else if (!vm.AppVM.HasPendingImportWarning)
                vm.Status = $"Imported \"{displayName}\".";
        }
        finally
        {
            try { File.Delete(tempZipPath); } catch { /* best-effort cleanup */ }
        }
    }

    private async void OnConfirmImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptureViewModel vm) return;
        await vm.AppVM.ConfirmPendingImportAsync();
        vm.Status = "Translation imported.";
    }

    private void OnCancelImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptureViewModel vm) return;
        vm.AppVM.CancelPendingImport();
    }

    private async void OnDeleteTranslationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string translationId }) return;
        if (DataContext is not ScriptureViewModel vm) return;

        var result = await vm.AppVM.DeleteTranslationAsync(translationId);
        vm.Status = result.IsSuccess ? "Translation deleted." : $"Failed to delete: {result.ErrorMessage}";
    }

    private void OnRenameTranslationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TranslationListItem item }) return;
        item.PendingName = item.DisplayName;
        item.IsRenaming = true;
    }

    private async void OnSaveRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TranslationListItem item }) return;
        if (DataContext is not ScriptureViewModel vm) return;

        var newName = item.PendingName;
        var result = await vm.AppVM.RenameTranslationAsync(item.Id, newName);
        vm.Status = result.IsSuccess ? "Translation renamed." : $"Failed to rename: {result.ErrorMessage}";
        // On success, RenameTranslationAsync's RefreshTranslationsAsync rebuilds the whole
        // InstalledTranslations collection with fresh TranslationListItem instances (IsRenaming
        // defaults to false), so this item's editing state is discarded either way — no need to
        // explicitly reset IsRenaming here on the success path. On failure, reset explicitly:
        if (!result.IsSuccess)
            item.IsRenaming = false;
    }

    private void OnCancelRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TranslationListItem item }) return;
        item.PendingName = item.DisplayName;
        item.IsRenaming = false;
    }
```

Check the top of `MainView.axaml.cs` for existing `using Avalonia.Platform.Storage;` / `using Avalonia;` (needed for `TopLevel`, `FilePickerOpenOptions`, `FilePickerFileType`) — add whichever is missing.

- [ ] **Step 5: Manual verification**

Run the app (`dotnet run --project MyBibleApp.Desktop`):
1. Build a test ZIP with 2-3 `.usx` files (reuse fixtures from Task 2's tests, or hand-craft one) containing a `<book code="...">` for a couple of books.
2. Open Settings → click "Import Translation ZIP…" → pick the test ZIP.
3. If the ZIP is missing books, confirm the "Some books are missing" warning appears with working "Import Anyway"/"Cancel" buttons.
4. Confirm the imported translation now appears as a radio option, selecting it re-renders the currently open book from the imported translation's files (or shows a "not available"/error state for books missing from that translation — there is no dedicated missing-book UI state yet; `TryLoadBookFromApiAsync`'s existing `(false, ex.Message)` failure path surfaces `FileNotFoundException`'s message via whatever currently displays load failures for the BSB API-failure case).
5. Switch back to "BSB (online)" — confirm BSB content loads again.
6. Click "Rename" on the imported translation, change the name, click "Save" — confirm the new name persists (reopen Settings to confirm it survived the refresh). Click "Rename" again, then "Cancel" — confirm the name is unchanged.
7. Delete the imported translation from Settings — confirm it disappears from the list and, if it was active, the app falls back to BSB.

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/ViewModels/AppViewModel.cs MyBibleApp/ViewModels/TranslationListItem.cs MyBibleApp/Views/MainView.axaml MyBibleApp/Views/MainView.axaml.cs MyBibleApp/Views/AppShellView.axaml.cs MyBibleApp/Services/TranslationManager.cs
git commit -m "feat: add Settings UI for importing, switching, and managing translations"
```
