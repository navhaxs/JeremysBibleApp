using Avalonia.Media;

namespace MyBibleApp.Controls;

public static class BibleBookGroups
{
    private record GroupColors(Color LabelBg, Color LabelFg, Color CellColor);

    private static readonly GroupColors Pentateuch    = new(Color.Parse("#4A5E2E"), Color.Parse("#B8CE80"), Color.Parse("#6A8A4A"));
    private static readonly GroupColors Historical    = new(Color.Parse("#7A8A2E"), Color.Parse("#D8E880"), Color.Parse("#9AAA4A"));
    private static readonly GroupColors Poetry        = new(Color.Parse("#4E3C82"), Color.Parse("#C8BEFF"), Color.Parse("#6E5CA8"));
    private static readonly GroupColors MajorProphets = new(Color.Parse("#1E3A6E"), Color.Parse("#9AB8F0"), Color.Parse("#3A5A90"));
    private static readonly GroupColors MinorProphets = new(Color.Parse("#4E5E22"), Color.Parse("#C0D070"), Color.Parse("#6E7E38"));
    private static readonly GroupColors GospelsActs   = new(Color.Parse("#921870"), Color.Parse("#FFB0E8"), Color.Parse("#C03898"));
    private static readonly GroupColors PaulsLetters  = new(Color.Parse("#9A4A08"), Color.Parse("#FFD080"), Color.Parse("#CC6A20"));
    private static readonly GroupColors GeneralRev    = new(Color.Parse("#8A6A00"), Color.Parse("#F0D060"), Color.Parse("#B89010"));

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
