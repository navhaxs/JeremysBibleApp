using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class UsxBibleApiLoader
{
    private const string BaseUrl = "https://v1.fetch.bible/bibles/eng_bsb/usx/";

    private static readonly HttpClient HttpClient = new();
    private static readonly ConcurrentDictionary<string, string> UsxCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly UsxBibleParser _parser;

    public UsxBibleApiLoader(UsxBibleParser parser)
    {
        _parser = parser;
    }

    public async Task<BibleBook> LoadFromApiAsync(string bookCode)
    {
        if (string.IsNullOrWhiteSpace(bookCode))
            throw new ArgumentException("Book code is required.", nameof(bookCode));

        var normalizedCode = bookCode.Trim().ToLowerInvariant();
        if (!UsxCache.TryGetValue(normalizedCode, out var xml))
        {
            var uri = new Uri($"{BaseUrl}{normalizedCode}.usx", UriKind.Absolute);
            using var response = await HttpClient.GetAsync(uri).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"API request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");

            xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            UsxCache[normalizedCode] = xml;
        }

        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return _parser.Parse(document);
    }
}

