using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Platform;

namespace MyBibleApp.Services;

public sealed class JsonBookNameProvider : IBookNameProvider
{
    private readonly Dictionary<string, string> _bookNames = new(StringComparer.OrdinalIgnoreCase);

    public JsonBookNameProvider(string assetUri)
    {
        try
        {
            var uri = new Uri(assetUri, UriKind.Absolute);
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var document = JsonDocument.Parse(json);
            
            if (document.RootElement.TryGetProperty("book_names_english", out var bookNamesElement))
            {
                foreach (var property in bookNamesElement.EnumerateObject())
                {
                    _bookNames[property.Name] = property.Value.GetString() ?? property.Name;
                }
            }
        }
        catch (Exception ex)
        {
            // Fallback to empty dictionary; codes will be returned as names
            System.Diagnostics.Debug.WriteLine($"Failed to load book names: {ex.Message}");
        }
    }

    public string GetEnglishName(string bookCode)
    {
        return _bookNames.TryGetValue(bookCode, out var englishName) ? englishName : bookCode;
    }
}
