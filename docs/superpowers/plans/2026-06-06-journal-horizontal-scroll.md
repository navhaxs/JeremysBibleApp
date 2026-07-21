# Journal Mode Horizontal Scroll Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When journal mode is active on mobile, enable horizontal scrolling so text is never forced to wrap narrower than the journal's fixed column width, keeping pen annotations aligned.

**Architecture:** Wrap `InkAreaGrid` (which contains both `InkOverlayCanvas` and `ParagraphList`) in a new outer `ScrollViewer` so both layers scroll together. Set `InkAreaGrid.MinWidth = layout.TextColumnWidthDip` in `SetJournalLayout` to force the content to its intended width, triggering the scrollbar when viewport is narrower.

**Tech Stack:** Avalonia UI, C# — `ScrollViewer`, `ScrollBarVisibility` from `Avalonia.Controls.Primitives` (already imported)

---

### Task 1: Add `ScrollViewer` wrapper in XAML

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml:477-483`

- [ ] **Step 1: Replace the `InkAreaGrid` opening tag block**

In `MainView.axaml`, find:
```xml
            <!--  Main content grid (row 2) with overlay  -->
            <Grid
                Background="{DynamicResource ThemeDotPatternBrush}"
                Grid.Row="2"
                Margin="0,0,0,0"
                RowDefinitions="*"
                x:Name="InkAreaGrid">
```

Replace with:
```xml
            <!--  Main content grid (row 2) with overlay  -->
            <ScrollViewer
                Grid.Row="2"
                HorizontalScrollBarVisibility="Disabled"
                VerticalScrollBarVisibility="Disabled"
                x:Name="ContentHScrollContainer">
            <Grid
                Background="{DynamicResource ThemeDotPatternBrush}"
                Margin="0,0,0,0"
                RowDefinitions="*"
                x:Name="InkAreaGrid">
```

Note: `Grid.Row="2"` moves up to `ScrollViewer`; `InkAreaGrid` no longer needs it.

- [ ] **Step 2: Close the `ScrollViewer` after `InkAreaGrid`'s closing tag**

Find the closing tag of `InkAreaGrid` (the `</Grid>` that closes the grid containing `PenUnderlay` and `ParagraphList`). Add `</ScrollViewer>` immediately after it.

The structure should end as:
```xml
            </Grid>  <!-- closes InkAreaGrid -->
            </ScrollViewer>  <!-- closes ContentHScrollContainer -->
```

- [ ] **Step 3: Build and verify no XAML parse errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```
Expected: Build succeeds with 0 errors.

---

### Task 2: Wire up the new `ScrollViewer` in code-behind and update `SetJournalLayout`

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml.cs`

- [ ] **Step 1: Add field for the new scroll container**

Find the existing field block (around line 79):
```csharp
    private Grid? _inkAreaGrid;
```

Add directly below it:
```csharp
    private ScrollViewer? _contentHScrollContainer;
```

- [ ] **Step 2: Wire up the reference**

Find the existing `FindControl` call (around line 213):
```csharp
        _inkAreaGrid    = this.FindControl<Grid>("InkAreaGrid");
```

Add directly below it:
```csharp
        _contentHScrollContainer = this.FindControl<ScrollViewer>("ContentHScrollContainer");
```

- [ ] **Step 3: Update `SetJournalLayout` — journal active branch**

Find the `layout != null` branch inside `SetJournalLayout` (around line 2193):
```csharp
        else
        {
            if (layout.TextColumnWidthDip > 0)
                _paragraphList.MaxWidth = layout.TextColumnWidthDip;

            if (layout.FontSizeDip > 0)
                _paragraphList.FontSize = layout.FontSizeDip;

            if (!string.IsNullOrEmpty(layout.FontFamily))
                _paragraphList.FontFamily = new Avalonia.Media.FontFamily(layout.FontFamily);
        }
```

Replace with:
```csharp
        else
        {
            if (layout.TextColumnWidthDip > 0)
            {
                _paragraphList.MaxWidth = layout.TextColumnWidthDip;
                if (_inkAreaGrid != null) _inkAreaGrid.MinWidth = layout.TextColumnWidthDip;
                if (_contentHScrollContainer != null)
                    _contentHScrollContainer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            }

            if (layout.FontSizeDip > 0)
                _paragraphList.FontSize = layout.FontSizeDip;

            if (!string.IsNullOrEmpty(layout.FontFamily))
                _paragraphList.FontFamily = new Avalonia.Media.FontFamily(layout.FontFamily);
        }
```

- [ ] **Step 4: Update `SetJournalLayout` — journal inactive branch**

Find the `layout == null` branch (around line 2185):
```csharp
        if (layout == null)
        {
            // Restore defaults
            _paragraphList.MaxWidth = double.PositiveInfinity;
            _paragraphList.FontSize = 19;
            _paragraphList.ClearValue(FontFamilyProperty);
        }
```

Replace with:
```csharp
        if (layout == null)
        {
            // Restore defaults
            _paragraphList.MaxWidth = double.PositiveInfinity;
            _paragraphList.FontSize = 19;
            _paragraphList.ClearValue(FontFamilyProperty);
            if (_inkAreaGrid != null) _inkAreaGrid.MinWidth = 0;
            if (_contentHScrollContainer != null)
                _contentHScrollContainer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
```

- [ ] **Step 5: Build and verify no errors**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```
Expected: Build succeeds with 0 errors.

- [ ] **Step 6: Commit**

```bash
git add MyBibleApp/Views/MainView.axaml MyBibleApp/Views/MainView.axaml.cs
git commit -m "feat: enable horizontal scroll in journal mode to prevent annotation misalignment"
```

---

### Task 3: Manual verification

- [ ] **Step 1: Run app on a narrow viewport (mobile device or simulator)**

Activate a journal. Narrow the window (or run on device) to a width less than the journal's column width.

Expected:
- Text does NOT wrap to viewport width — it holds its column width
- A horizontal scrollbar / pan gesture appears
- Pen annotations remain aligned with the text at their original positions
- Vertical scrolling still works normally (ListBox internal scroll unaffected)

- [ ] **Step 2: Verify inactive journal state**

Deactivate the journal (or select no journal).

Expected:
- Text wraps to viewport width as before
- No horizontal scroll
- No visible scrollbar

- [ ] **Step 3: Verify desktop / wide viewport**

On desktop or a window wider than the journal column width, activate a journal.

Expected:
- No horizontal scrollbar appears (content fits)
- No layout regression
