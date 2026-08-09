using System.IO;
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
        if (!File.Exists(path))
            throw new FileNotFoundException($"Book '{normalizedCode}' is not available in this translation.", path);

        var xml = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return _parser.Parse(document);
    }
}
