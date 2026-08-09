using System;
using System.Collections.Generic;

namespace MyBibleApp.Models;

public sealed class InstalledTranslation
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SourceZipName { get; init; } = string.Empty;
    public DateTime ImportedAtUtc { get; init; }
    public IReadOnlyList<string> BookCodes { get; init; } = [];
    public IReadOnlyList<string> MissingBookCodes { get; init; } = [];
}
