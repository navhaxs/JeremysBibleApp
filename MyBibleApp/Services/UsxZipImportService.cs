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
    internal const long MaxUncompressedBytes = 200L * 1024 * 1024;

    private long _maxUncompressedBytesForTest = MaxUncompressedBytes;

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

    internal UsxZipImportService(UsxBibleParser? parser, long maxUncompressedBytesForTest)
    {
        _parser = parser ?? new UsxBibleParser();
        _maxUncompressedBytesForTest = maxUncompressedBytesForTest;
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

            var destinationPath = Path.GetFullPath(Path.Combine(tempDir, flatName));
            if (!destinationPath.StartsWith(normalizedTempDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue; // zip-slip guard

            try
            {
                // Manual extraction with actual byte counting to prevent decompression-bomb attacks
                using (var entryStream = entry.Open())
                using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        totalUncompressed += bytesRead;
                        if (totalUncompressed > _maxUncompressedBytesForTest)
                        {
                            try { File.Delete(destinationPath); } catch { }
                            throw new InvalidOperationException($"ZIP exceeds the {_maxUncompressedBytesForTest / (1024 * 1024)}MB uncompressed size limit.");
                        }
                        fileStream.Write(buffer, 0, bytesRead);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Size cap exceeded - propagate this error
                throw;
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
                // Delete the extracted file to avoid leaving garbage on disk
                try { File.Delete(destinationPath); } catch { }
            }

            if (string.IsNullOrWhiteSpace(code))
                continue;

            var normalizedCode = code.Trim().ToLowerInvariant();
            var finalPath = Path.Combine(tempDir, $"{normalizedCode}.usx");
            // Ordinal, not OrdinalIgnoreCase: a case-only difference (e.g. zip entry "GEN.usx"
            // vs target "gen.usx") must still trigger the rename. On case-insensitive
            // filesystems (Windows) skipping it was invisible — File.Exists("gen.usx") matches
            // "GEN.usx" anyway — but on case-sensitive ones (Android's ext4) the un-renamed
            // uppercase file made every zip-imported book "not found" at load time.
            if (!string.Equals(destinationPath, finalPath, StringComparison.Ordinal))
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
        var manifestPath = Path.Combine(finalDir, "manifest.json");
        // Use atomic write pattern: write to temp file, then move into place
        WriteAtomically(manifestPath, json);

        return manifest;
    }

    public void CancelImport(PreparedTranslationImport prepared)
    {
        if (Directory.Exists(prepared.TempDirectory))
            Directory.Delete(prepared.TempDirectory, recursive: true);
    }

    private static void WriteAtomically(string filePath, string content)
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);
    }
}
