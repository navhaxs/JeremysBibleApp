using System;
using System.Net.Http;
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

    public BibleBook LoadFromApi(string bookCode)
    {
        if (string.IsNullOrWhiteSpace(bookCode))
            throw new ArgumentException("Book code is required.", nameof(bookCode));

        var normalizedCode = bookCode.Trim().ToLowerInvariant();
        var uri = new Uri($"{BaseUrl}{normalizedCode}.usx", UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = HttpClient.Send(request);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"API request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");

        var xml = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return _parser.Parse(document);
    }
}

