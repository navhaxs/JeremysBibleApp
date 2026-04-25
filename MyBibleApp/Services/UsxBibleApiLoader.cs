using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class UsxBibleApiLoader
{
    private const string BaseUrl = "https://v1.fetch.bible/bibles/eng_bsb/usx/";

    private static readonly HttpClient HttpClient = new();
    private static readonly ConcurrentDictionary<string, string> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string DiskCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MyBibleApp", "UsxCache");

    private readonly UsxBibleParser _parser;

    public UsxBibleApiLoader(UsxBibleParser parser)
    {
        _parser = parser;
    }

    public async Task<BibleBook> LoadFromApiAsync(string bookCode)
    {
        if (string.IsNullOrWhiteSpace(bookCode))
            throw new ArgumentException("Book code is required.", nameof(bookCode));

        var xml = await GetXmlAsync(bookCode).ConfigureAwait(false);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return _parser.Parse(document);
    }

    /// <summary>
    /// Downloads any books not yet on disk, sequentially in the background.
    /// Already-cached books are skipped. Cancellation stops after the current book finishes.
    /// </summary>
    public async Task PrefetchAllBooksAsync(IEnumerable<string> bookCodes, CancellationToken cancellationToken = default)
    {
        foreach (var code in bookCodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedCode = code.Trim().ToLowerInvariant();
            if (IsCachedOnDisk(normalizedCode))
                continue;

            try
            {
                await GetXmlAsync(normalizedCode).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UsxBibleApiLoader] Prefetch failed for '{normalizedCode}': {ex.Message}");
            }
        }
    }

    private async Task<string> GetXmlAsync(string bookCode)
    {
        var normalizedCode = bookCode.Trim().ToLowerInvariant();

        if (MemoryCache.TryGetValue(normalizedCode, out var cached))
            return cached;

        var diskPath = GetDiskCachePath(normalizedCode);
        if (File.Exists(diskPath))
        {
            var diskXml = await File.ReadAllTextAsync(diskPath).ConfigureAwait(false);
            MemoryCache[normalizedCode] = diskXml;
            return diskXml;
        }

        var uri = new Uri($"{BaseUrl}{normalizedCode}.usx", UriKind.Absolute);
        using var response = await HttpClient.GetAsync(uri).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"API request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");

        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        await WriteToDiskCacheAsync(normalizedCode, xml).ConfigureAwait(false);
        MemoryCache[normalizedCode] = xml;

        return xml;
    }

    private static bool IsCachedOnDisk(string normalizedCode) =>
        File.Exists(GetDiskCachePath(normalizedCode));

    private static string GetDiskCachePath(string normalizedCode) =>
        Path.Combine(DiskCacheDirectory, $"{normalizedCode}.usx");

    private static async Task WriteToDiskCacheAsync(string normalizedCode, string xml)
    {
        try
        {
            Directory.CreateDirectory(DiskCacheDirectory);
            var finalPath = GetDiskCachePath(normalizedCode);

            // Write to a unique temp file then move atomically so concurrent writers
            // don't collide on the same file handle.
            var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tempPath, xml).ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UsxBibleApiLoader] Failed to write disk cache for '{normalizedCode}': {ex.Message}");
        }
    }
}

