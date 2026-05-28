using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class ContentHashService : IContentHashService
{
    /// <inheritdoc />
    public string ComputeHash(IReadOnlyList<BibleParagraph> paragraphs)
    {
        var combined = new StringBuilder();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            combined.Append(paragraphs[i].Text);
        }

        var bytes = Encoding.UTF8.GetBytes(combined.ToString());
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <inheritdoc />
    public bool Verify(IReadOnlyList<BibleParagraph> paragraphs, string storedHash)
    {
        var currentHash = ComputeHash(paragraphs);
        return string.Equals(currentHash, storedHash, StringComparison.Ordinal);
    }
}
