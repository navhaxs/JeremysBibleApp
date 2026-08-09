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

    public string GetTranslationsRootForCommit() => _translationsRoot;

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

            // Deduplicate by Id (keep first-seen): two folders can carry manifests with the
            // same Id after a manual folder copy or a bad sync restore, and callers key
            // dictionaries by Id — a duplicate would throw and brick translation loading.
            return (IReadOnlyList<InstalledTranslation>)results
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
