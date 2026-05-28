using System.Collections.Generic;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public interface IContentHashService
{
    /// <summary>
    /// Computes a deterministic SHA-256 hash of the Bible text for a given passage and translation.
    /// </summary>
    string ComputeHash(IReadOnlyList<BibleParagraph> paragraphs);

    /// <summary>
    /// Verifies that the current text matches a previously stored hash.
    /// </summary>
    bool Verify(IReadOnlyList<BibleParagraph> paragraphs, string storedHash);
}
