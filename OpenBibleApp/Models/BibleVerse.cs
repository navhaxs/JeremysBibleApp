namespace OpenBibleApp.Models;

public sealed record BibleVerse(int Chapter, string Number, string Text)
{
    public string DisplayText => $"{Chapter}:{Number} {Text}";
}

