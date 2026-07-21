# Bible Reading Chart — Canonical Group Color Coding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Color-code book name labels and read chapter cells by canonical section (Pentateuch, Historical, Poetry, etc.) to match the reference Bible reading chart visual style.

**Architecture:** Add a static color lookup class (`BibleBookGroups`) that maps canonical book index to group colors as plain `Color` structs. `BibleReadingBookEntry` gains three color properties (label bg, label fg, cell color) set from the lookup. `ChapterGridControl` gains a `BookCellColor` styled property used in `Render()` instead of the theme accent. The AXAML binds these properties to style the label and cells. No new layout nodes — zero perf regression.

**Tech Stack:** C# 13, .NET 10, Avalonia UI 11, ReactiveUI. No new NuGet packages needed.

**Note on tests:** `BibleBookGroups` is a hardcoded lookup table (configuration, not business logic). No existing test project references `MyBibleApp`. Visual verification is the appropriate test here — run the app and confirm colors appear correctly per section.

---

## File Map

| Action | File | Responsibility |
|---|---|---|
| Create | `MyBibleApp/Controls/BibleBookGroups.cs` | Static color lookup — maps (isOt, index) → group colors |
| Modify | `MyBibleApp/ViewModels/BibleReadingBookEntry.cs` | Add `LabelBackground`, `LabelForeground`, `ReadCellColor` properties |
| Modify | `MyBibleApp/ViewModels/BibleReadingViewModel.cs` | Pass `isOt` + per-testament index to `BibleReadingBookEntry` |
| Modify | `MyBibleApp/Controls/ChapterGridControl.cs` | Add `BookCellColor` styled property + cached brush |
| Modify | `MyBibleApp/Views/BibleReadingView.axaml` | Bind label background/foreground + cell color; remove `CornerRadius` from book-card |

---

### Task 1: Create `BibleBookGroups` color lookup

**Files:**
- Create: `MyBibleApp/Controls/BibleBookGroups.cs`

- [ ] **Step 1: Create the file**

```csharp
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
```

- [ ] **Step 2: Build to confirm no errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```
git add MyBibleApp/Controls/BibleBookGroups.cs
git commit -m "feat: add BibleBookGroups canonical color lookup"
```

---

### Task 2: Add color properties to `BibleReadingBookEntry`

**Files:**
- Modify: `MyBibleApp/ViewModels/BibleReadingBookEntry.cs`

Current constructor signature: `BibleReadingBookEntry(string code, string name, int chapterCount)`

- [ ] **Step 1: Update the file**

Replace the entire file content:

```csharp
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using MyBibleApp.Controls;

namespace MyBibleApp.ViewModels;

public class BibleReadingBookEntry
{
    public string Code { get; }
    public string Name { get; }
    public IReadOnlyList<BibleReadingChapterCell> Chapters { get; }

    public IBrush LabelBackground { get; }
    public IBrush LabelForeground { get; }
    public Color ReadCellColor { get; }

    public BibleReadingBookEntry(string code, string name, int chapterCount, bool isOt, int bookIndex)
    {
        Code = code;
        Name = name;
        Chapters = Enumerable.Range(1, chapterCount)
            .Select(i => new BibleReadingChapterCell(code, i))
            .ToList();

        var (labelBg, labelFg, cellColor) = BibleBookGroups.GetGroupColors(isOt, bookIndex);
        LabelBackground = new SolidColorBrush(labelBg);
        LabelForeground = new SolidColorBrush(labelFg);
        ReadCellColor = cellColor;
    }
}
```

- [ ] **Step 2: Build (will fail — ViewModel not updated yet)**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: CS7036 — `BibleReadingBookEntry` missing required arguments. This is expected; fix in Task 3.

---

### Task 3: Update `BibleReadingViewModel` to pass `isOt` + index

**Files:**
- Modify: `MyBibleApp/ViewModels/BibleReadingViewModel.cs` (lines ~137–142, the `LoadBooks` yield loop)

The current code in `LoadBooks()` ends with:

```csharp
foreach (var code in orderedCodes)
{
    var name     = names.TryGetValue(code, out var n) ? n : code;
    var chapters = chapterCounts.TryGetValue(code, out var c) ? c : 1;
    yield return new BibleReadingBookEntry(code, name, chapters);
}
```

- [ ] **Step 1: Add index tracking and replace the yield**

Replace that foreach block with:

```csharp
var index = 0;
foreach (var code in orderedCodes)
{
    var name     = names.TryGetValue(code, out var n) ? n : code;
    var chapters = chapterCounts.TryGetValue(code, out var c) ? c : 1;
    var isOt     = index < 39;
    var bookIndex = isOt ? index : index - 39;
    yield return new BibleReadingBookEntry(code, name, chapters, isOt, bookIndex);
    index++;
}
```

- [ ] **Step 2: Build to confirm fix**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```
git add MyBibleApp/ViewModels/BibleReadingBookEntry.cs
git add MyBibleApp/ViewModels/BibleReadingViewModel.cs
git commit -m "feat: add canonical group color properties to BibleReadingBookEntry"
```

---

### Task 4: Add `BookCellColor` styled property to `ChapterGridControl`

**Files:**
- Modify: `MyBibleApp/Controls/ChapterGridControl.cs`

- [ ] **Step 1: Add the styled property and cached brush field**

After the existing `ChaptersProperty` declaration (around line 35), add:

```csharp
public static readonly StyledProperty<Color> BookCellColorProperty =
    AvaloniaProperty.Register<ChapterGridControl, Color>(nameof(BookCellColor));

public Color BookCellColor
{
    get => GetValue(BookCellColorProperty);
    set => SetValue(BookCellColorProperty, value);
}

private SolidColorBrush? _cachedCellBrush;
```

- [ ] **Step 2: Register `AffectsRender` and cache rebuild in `static` constructor and `OnPropertyChanged`**

In the `static ChapterGridControl()` constructor, add `BookCellColorProperty` to `AffectsRender`:

```csharp
static ChapterGridControl()
{
    AffectsRender<ChapterGridControl>(ChaptersProperty);
    AffectsMeasure<ChapterGridControl>(ChaptersProperty);
    AffectsRender<ChapterGridControl>(BookCellColorProperty);
}
```

In `OnPropertyChanged`, add a branch after the `ChaptersProperty` check:

```csharp
if (change.Property == BookCellColorProperty)
{
    _cachedCellBrush = new SolidColorBrush(BookCellColor);
    InvalidateVisual();
}
```

- [ ] **Step 3: Use `_cachedCellBrush` in `Render()`**

In the `Render()` method, find the line:

```csharp
var accentBrush = GetResourceBrush("ThemeAccentColor") ?? Brushes.DodgerBlue;
```

Replace the background-drawing block for `IsRead` cells (around line 152–155):

```csharp
// Background
if (cell.IsRead)
{
    var brush = _cachedCellBrush ?? accentBrush;
    context.DrawRectangle(brush, null, rect);
}
```

- [ ] **Step 4: Build to confirm no errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```
git add MyBibleApp/Controls/ChapterGridControl.cs
git commit -m "feat: add BookCellColor styled property to ChapterGridControl"
```

---

### Task 5: Update `BibleReadingView.axaml` — bind colors and remove CornerRadius

**Files:**
- Modify: `MyBibleApp/Views/BibleReadingView.axaml`

- [ ] **Step 1: Remove `CornerRadius` from `book-card` style**

Find in `<UserControl.Styles>`:

```xml
<Style Selector="Border.book-card">
  <Setter Property="BorderThickness" Value="1" />
  <Setter Property="BorderBrush" Value="{DynamicResource SystemControlForegroundBaseMediumLowBrush}" />
  <Setter Property="CornerRadius" Value="4" />
  <Setter Property="Margin" Value="6,4" />
  <Setter Property="Padding" Value="8,6" />
</Style>
```

Remove the `CornerRadius` setter line:

```xml
<Style Selector="Border.book-card">
  <Setter Property="BorderThickness" Value="1" />
  <Setter Property="BorderBrush" Value="{DynamicResource SystemControlForegroundBaseMediumLowBrush}" />
  <Setter Property="Margin" Value="6,4" />
  <Setter Property="Padding" Value="8,6" />
</Style>
```

- [ ] **Step 2: Update the OT book DataTemplate**

Find the OT `DataTemplate` (around line 93). Replace the inner `Grid` content:

Current:
```xml
<DataTemplate x:DataType="vm:BibleReadingBookEntry">
  <Border Classes="book-card">
    <Grid ColumnDefinitions="110,Auto" ColumnSpacing="8">
      <TextBlock Grid.Column="0"
                 Text="{Binding Name}"
                 FontSize="13"
                 VerticalAlignment="Top"
                 TextWrapping="Wrap" />
      <controls:ChapterGridControl Grid.Column="1"
                                    Chapters="{Binding Chapters}" />
    </Grid>
  </Border>
</DataTemplate>
```

Replace with:
```xml
<DataTemplate x:DataType="vm:BibleReadingBookEntry">
  <Border Classes="book-card">
    <Grid ColumnDefinitions="110,Auto" ColumnSpacing="8">
      <Border Grid.Column="0"
              Background="{Binding LabelBackground}"
              CornerRadius="2"
              Padding="4,2"
              VerticalAlignment="Top">
        <TextBlock Text="{Binding Name}"
                   Foreground="{Binding LabelForeground}"
                   FontSize="12"
                   TextWrapping="Wrap" />
      </Border>
      <controls:ChapterGridControl Grid.Column="1"
                                    Chapters="{Binding Chapters}"
                                    BookCellColor="{Binding ReadCellColor}" />
    </Grid>
  </Border>
</DataTemplate>
```

- [ ] **Step 3: Apply the same update to the NT DataTemplate**

Find the NT `DataTemplate` (around line 125). Apply identical replacement:

```xml
<DataTemplate x:DataType="vm:BibleReadingBookEntry">
  <Border Classes="book-card">
    <Grid ColumnDefinitions="110,Auto" ColumnSpacing="8">
      <Border Grid.Column="0"
              Background="{Binding LabelBackground}"
              CornerRadius="2"
              Padding="4,2"
              VerticalAlignment="Top">
        <TextBlock Text="{Binding Name}"
                   Foreground="{Binding LabelForeground}"
                   FontSize="12"
                   TextWrapping="Wrap" />
      </Border>
      <controls:ChapterGridControl Grid.Column="1"
                                    Chapters="{Binding Chapters}"
                                    BookCellColor="{Binding ReadCellColor}" />
    </Grid>
  </Border>
</DataTemplate>
```

- [ ] **Step 4: Build the full solution**

```
dotnet build
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Run the app and verify visually**

Launch the app, open "My Bible Reading". Verify:
- Genesis–Deuteronomy labels: dark olive green background, light green text
- Joshua–Esther labels: yellow-green background
- Job–Song of Songs labels: purple background
- Isaiah–Daniel labels: dark blue background
- Hosea–Malachi labels: forest green background
- Matthew–Acts labels: dark pink/magenta background
- Romans–Philemon labels: amber/orange background
- Hebrews–Revelation labels: yellow-green background
- Read chapters show matching section color (not the theme accent)
- Unread chapters stay neutral
- Panning/dragging is smooth on Android

- [ ] **Step 6: Commit**

```
git add MyBibleApp/Views/BibleReadingView.axaml
git commit -m "feat: bind canonical group colors to book labels and chapter cells"
```
