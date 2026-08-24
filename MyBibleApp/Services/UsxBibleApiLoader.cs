using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class UsxBibleApiLoader
{
    // Stable tag so `adb logcat | grep MBA_STARTUP` isolates this from everything else.
    // Console.WriteLine (not Debug.WriteLine) so it survives Release-build device testing,
    // where Debug.WriteLine calls are compiled out.
    private const string StartupLogTag = "MBA_STARTUP";

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

        var sw = Stopwatch.StartNew();
        var xml = await GetXmlAsync(bookCode).ConfigureAwait(false);
        var xmlReadyMs = sw.ElapsedMilliseconds;

        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var xdocParsedMs = sw.ElapsedMilliseconds;

        var book = _parser.Parse(document);
        var totalMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"[{StartupLogTag}]    UsxBibleApiLoader({bookCode}): xml-ready={xmlReadyMs}ms " +
            $"xdoc-parse={xdocParsedMs - xmlReadyMs}ms usx-parse={totalMs - xdocParsedMs}ms total={totalMs}ms " +
            $"({book.Paragraphs.Count} paragraphs, {xml.Length} chars)");

        return book;
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
        {
            Console.WriteLine($"[{StartupLogTag}]    GetXmlAsync({normalizedCode}): memory-cache hit");
            return cached;
        }

        var diskPath = GetDiskCachePath(normalizedCode);
        if (File.Exists(diskPath))
        {
            var sw = Stopwatch.StartNew();
            var diskXml = await File.ReadAllTextAsync(diskPath).ConfigureAwait(false);
            Console.WriteLine($"[{StartupLogTag}]    GetXmlAsync({normalizedCode}): disk-cache read in {sw.ElapsedMilliseconds}ms ({diskXml.Length} chars)");
            MemoryCache[normalizedCode] = diskXml;
            return diskXml;
        }

        Console.WriteLine($"[{StartupLogTag}]    GetXmlAsync({normalizedCode}): no cache — fetching from network ({BaseUrl}{normalizedCode}.usx)");
        var netSw = Stopwatch.StartNew();
        var uri = new Uri($"{BaseUrl}{normalizedCode}.usx", UriKind.Absolute);
        using var response = await HttpClient.GetAsync(uri).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"API request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");

        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Console.WriteLine($"[{StartupLogTag}]    GetXmlAsync({normalizedCode}): network fetch finished in {netSw.ElapsedMilliseconds}ms ({xml.Length} chars)");

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

