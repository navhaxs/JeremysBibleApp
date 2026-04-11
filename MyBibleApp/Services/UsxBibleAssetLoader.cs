using System;
using System.Xml.Linq;
using Avalonia.Platform;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class UsxBibleAssetLoader : IUsxBibleLoader
{
    private readonly UsxBibleParser _parser;

    public UsxBibleAssetLoader(UsxBibleParser parser)
    {
        _parser = parser;
    }

    public BibleBook LoadFromAsset(string assetUri)
    {
        var uri = new Uri(assetUri, UriKind.Absolute);

        using var stream = AssetLoader.Open(uri);
        using var reader = new System.IO.StreamReader(stream);
        using var xmlReader = System.Xml.XmlReader.Create(reader);

        var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        return _parser.Parse(document);
    }
}

