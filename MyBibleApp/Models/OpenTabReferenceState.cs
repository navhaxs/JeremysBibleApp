namespace MyBibleApp.Models;

/// <summary>
/// Represents the current scripture reference shown in one open tab.
/// </summary>
public sealed class OpenTabReferenceState
{
    public int TabIndex { get; init; }

    public string Header { get; init; } = string.Empty;

    public string BookCode { get; init; } = string.Empty;

    public int Chapter { get; init; } = 1;

    public int Verse { get; init; } = 1;
}

