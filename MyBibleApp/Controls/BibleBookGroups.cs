using Avalonia.Media;

namespace MyBibleApp.Controls;

public static class BibleBookGroups
{
    private record GroupColors(Color LabelBg, Color LabelFg, Color CellColor);

    private static readonly GroupColors Pentateuch    = new(Color.Parse("#4A5E2E"), Color.Parse("#B8CE80"), Color.Parse("#6A8A4A"));
    private static readonly GroupColors Historical    = new(Color.Parse("#7A8A2E"), Color.Parse("#D8E880"), Color.Parse("#9AAA4A"));
    private static readonly GroupColors Poetry        = new(Color.Parse("#5A4A8A"), Color.Parse("#C8BEFF"), Color.Parse("#7A6AAA"));
    private static readonly GroupColors MajorProphets = new(Color.Parse("#2A4A7A"), Color.Parse("#A0C0F0"), Color.Parse("#4A6A9A"));
    private static readonly GroupColors MinorProphets = new(Color.Parse("#5A6A2E"), Color.Parse("#C0D070"), Color.Parse("#7A8A4A"));
    private static readonly GroupColors GospelsActs   = new(Color.Parse("#8A2A6A"), Color.Parse("#FFB0E0"), Color.Parse("#AA4A8A"));
    private static readonly GroupColors PaulsLetters  = new(Color.Parse("#8A5A18"), Color.Parse("#FFD080"), Color.Parse("#C07A28"));
    private static readonly GroupColors GeneralRev    = new(Color.Parse("#6A7A28"), Color.Parse("#D0E060"), Color.Parse("#8A9A48"));

    public static (Color LabelBg, Color LabelFg, Color CellColor) GetGroupColors(bool isOt, int bookIndex)
    {
        var g = isOt ? GetOtGroup(bookIndex) : GetNtGroup(bookIndex);
        return (g.LabelBg, g.LabelFg, g.CellColor);
    }

    private static GroupColors GetOtGroup(int i) => i switch
    {
        < 5  => Pentateuch,     // Gen–Deut      (0–4)
        < 17 => Historical,     // Josh–Esther   (5–16)
        < 22 => Poetry,         // Job–Song      (17–21)
        < 27 => MajorProphets,  // Isa–Dan       (22–26)
        _    => MinorProphets,  // Hos–Mal       (27–38)
    };

    private static GroupColors GetNtGroup(int i) => i switch
    {
        < 5  => GospelsActs,    // Matt–Acts     (0–4)
        < 18 => PaulsLetters,   // Rom–Phm       (5–17)
        _    => GeneralRev,     // Heb–Rev       (18–26)
    };
}
