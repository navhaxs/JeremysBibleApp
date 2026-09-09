using System;
using System.IO;
using System.Linq;
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
        var exists = File.Exists(path);

        if (!exists)
        {
            // Self-heal translations imported before file names were forced to lowercase
            // (a prior bug left them under the ZIP's original casing, e.g. "GEN.usx" — harmless
            // on Windows' case-insensitive filesystem, but invisible to this exact-case lookup
            // on Android's case-sensitive one). Fall back to a case-insensitive scan rather than
            // forcing every affected install to delete and re-import.
            var fallback = Directory.Exists(_translationFolder)
                ? Directory.EnumerateFiles(_translationFolder, "*.usx")
                    .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), normalizedCode, StringComparison.OrdinalIgnoreCase))
                : null;

            if (fallback != null)
            {
                System.Diagnostics.Debug.WriteLine($"[UsxBibleZipLoader] '{path}' missing, found case-mismatched fallback '{fallback}'.");
                path = fallback;
                exists = true;
            }
        }

        System.Diagnostics.Debug.WriteLine($"[UsxBibleZipLoader] LoadBookAsync('{bookCode}') -> '{path}' exists={exists}");

        if (!exists)
            throw new FileNotFoundException($"Book '{normalizedCode}' is not available in this translation.", path);

        var xml = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        System.Diagnostics.Debug.WriteLine($"[UsxBibleZipLoader] Read {xml.Length} chars from '{path}'.");

        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var book = _parser.Parse(document);
        System.Diagnostics.Debug.WriteLine($"[UsxBibleZipLoader] Parsed '{book.Code}': {book.Paragraphs.Count} paragraphs, {book.VerseCount} verses.");
        return book;
    }
}
