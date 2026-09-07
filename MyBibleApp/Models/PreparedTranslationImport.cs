using System.Collections.Generic;

namespace MyBibleApp.Models;

public sealed class PreparedTranslationImport
{
    public required string TempDirectory { get; init; }
    public required IReadOnlyList<string> BookCodes { get; init; }
    public required IReadOnlyList<string> MissingBookCodes { get; init; }
}
