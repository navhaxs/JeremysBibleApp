using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class UsxBibleApiLoader
{
    private const string BaseUrl = "https://v1.fetch.bible/bibles/eng_bsb/usx/";

    private static readonly HttpClient HttpClient = new();

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
        var uri = new Uri($"{BaseUrl}{normalizedCode}.usx", UriKind.Absolute);

        using var response = await HttpClient.GetAsync(uri).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"API request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");

        var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return _parser.Parse(document);
    }
}

