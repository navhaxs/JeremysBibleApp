using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace MyBibleApp.Views;

// Immutable data item for a single resource entry shown in the list.
public record ThemeResourceEntry(string Key, IBrush Brush, string Hex, string Category);

public partial class ThemeResourcesDebugView : UserControl
{
    // All brush/color resource keys defined in Avalonia's Simple theme Base.xaml.
    // Keys in ThemeDictionaries (resolve per light/dark variant) are marked with a *.
    private static readonly (string Key, string Category)[] KnownKeys =
    [
        // ── Theme-variant brushes (Default / Dark dictionaries in Base.xaml) ──
        ("ThemeBackgroundBrush",             "Theme"),
        ("ThemeBorderLowBrush",              "Theme"),
        ("ThemeBorderMidBrush",              "Theme"),
        ("ThemeBorderHighBrush",             "Theme"),
        ("ThemeControlLowBrush",             "Theme"),
        ("ThemeControlMidBrush",             "Theme"),
        ("ThemeControlMidHighBrush",         "Theme"),
        ("ThemeControlHighBrush",            "Theme"),
        ("ThemeControlVeryHighBrush",        "Theme"),
        ("ThemeControlHighlightLowBrush",    "Theme"),
        ("ThemeControlHighlightMidBrush",    "Theme"),
        ("ThemeControlHighlightHighBrush",   "Theme"),
        ("ThemeForegroundBrush",             "Theme"),
        ("HighlightBrush",                   "Theme"),
        ("HighlightBrush2",                  "Theme"),
        ("HyperlinkVisitedBrush",            "Theme"),
        ("RefreshVisualizerForeground",      "Theme"),
        ("RefreshVisualizerBackground",      "Theme"),
        ("CaptionButtonForeground",          "Theme"),
        ("CaptionButtonBackground",          "Theme"),
        ("CaptionButtonBorderBrush",         "Theme"),

        // ── Theme-variant colors ──────────────────────────────────────────────
        ("ThemeBackgroundColor",             "Color"),
        ("ThemeBorderLowColor",              "Color"),
        ("ThemeBorderMidColor",              "Color"),
        ("ThemeBorderHighColor",             "Color"),
        ("ThemeControlLowColor",             "Color"),
        ("ThemeControlMidColor",             "Color"),
        ("ThemeControlMidHighColor",         "Color"),
        ("ThemeControlHighColor",            "Color"),
        ("ThemeControlVeryHighColor",        "Color"),
        ("ThemeControlHighlightLowColor",    "Color"),
        ("ThemeControlHighlightMidColor",    "Color"),
        ("ThemeControlHighlightHighColor",   "Color"),
        ("ThemeForegroundColor",             "Color"),
        ("HighlightColor",                   "Color"),
        ("HighlightColor2",                  "Color"),
        ("HyperlinkVisitedColor",            "Color"),

        // ── Global (non-variant) brushes ─────────────────────────────────────
        ("ThemeAccentBrush",                 "Accent"),
        ("ThemeAccentBrush2",                "Accent"),
        ("ThemeAccentBrush3",                "Accent"),
        ("ThemeAccentBrush4",                "Accent"),
        ("ThemeForegroundLowBrush",          "Global"),
        ("HighlightForegroundBrush",         "Global"),
        ("ErrorBrush",                       "Global"),
        ("ErrorLowBrush",                    "Global"),
        ("ThemeControlTransparentBrush",     "Global"),
        ("DatePickerFlyoutPresenterHighlightFill", "Global"),
        ("TimePickerFlyoutPresenterHighlightFill", "Global"),
        ("NotificationCardBackgroundBrush",           "Notification"),
        ("NotificationCardInformationBackgroundBrush","Notification"),
        ("NotificationCardSuccessBackgroundBrush",    "Notification"),
        ("NotificationCardWarningBackgroundBrush",    "Notification"),
        ("NotificationCardErrorBackgroundBrush",      "Notification"),

        // ── Global colors ────────────────────────────────────────────────────
        ("ThemeAccentColor",                 "Color"),
        ("ThemeAccentColor2",                "Color"),
        ("ThemeAccentColor3",                "Color"),
        ("ThemeAccentColor4",                "Color"),
        ("ThemeForegroundLowColor",          "Color"),
        ("HighlightForegroundColor",         "Color"),
        ("ErrorColor",                       "Color"),
        ("ErrorLowColor",                    "Color"),

        // ── System accent (from OS / platform) ───────────────────────────────
        ("SystemAccentColor",                "System"),
        ("SystemAccentColorLight1",          "System"),
        ("SystemAccentColorLight2",          "System"),
        ("SystemAccentColorLight3",          "System"),
        ("SystemAccentColorDark1",           "System"),
        ("SystemAccentColorDark2",           "System"),
        ("SystemAccentColorDark3",           "System"),
    ];

    private List<ThemeResourceEntry> _allEntries = [];
    private string _filter = string.Empty;

    public ThemeResourcesDebugView()
    {
        InitializeComponent();
        // AttachedToVisualTree fires at construction time (IsVisible=false).
        // Loaded fires later, once layout is complete and the app is fully styled.
        Loaded += (_, _) => Refresh();
    }

    // ── Public refresh ────────────────────────────────────────────────────────

    public void Refresh()
    {
        _allEntries = CollectAll();

        // Diagnostic: show what we found and a quick spot-check on one key.
        var diag = this.FindControl<TextBlock>("DiagLabel");
        if (diag != null)
        {
            var app = Application.Current;
            bool spotCheck = app != null &&
                             ((IResourceNode)app).TryGetResource("ThemeBorderMidBrush", ThemeVariant.Default, out _);
            diag.Text = $"CollectAll → {_allEntries.Count} entries | " +
                        $"App.ActualThemeVariant={app?.ActualThemeVariant} | " +
                        $"spot-check ThemeBorderMidBrush/Default={spotCheck}";
        }

        ApplyFilter();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<ThemeResourceEntry> CollectAll()
    {
        var results = new List<ThemeResourceEntry>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var app = Application.Current;
        if (app == null) return results;

        // Use Application as the resource root and try all three ThemeVariants so
        // nothing is missed regardless of light/dark mode.
        var variants = new[] { ThemeVariant.Default, ThemeVariant.Light, ThemeVariant.Dark };

        foreach (var (key, category) in KnownKeys)
        {
            if (!seen.Add(key)) continue;

            object? raw = null;
            foreach (var v in variants)
            {
                if (((IResourceNode)app).TryGetResource(key, v, out raw) && raw != null)
                    break;
            }

            if (TryMakeEntry(key, raw, category) is { } entry)
                results.Add(entry);
        }

        // Also sweep Application.Current.Resources for any app-defined brushes/colors
        // (these live in the flat dictionary, not inside ThemeDictionaries).
        if (app.Resources is System.Collections.IDictionary appDict)
        {
            foreach (System.Collections.DictionaryEntry de in appDict)
            {
                var k = de.Key?.ToString();
                if (k is null || !seen.Add(k)) continue;
                if (TryMakeEntry(k, de.Value, "App") is { } entry)
                    results.Add(entry);
            }
        }

        results.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private static ThemeResourceEntry? TryMakeEntry(string key, object? value, string category)
    {
        IBrush? brush = value switch
        {
            IBrush b => b,
            Color  c => new SolidColorBrush(c),
            _        => null
        };
        if (brush is null) return null;

        string hex = brush is SolidColorBrush scb
            ? $"#{scb.Color.A:X2}{scb.Color.R:X2}{scb.Color.G:X2}{scb.Color.B:X2}"
            : brush.GetType().Name;

        return new ThemeResourceEntry(key, brush, hex, category);
    }

    private void ApplyFilter()
    {
        var list  = this.FindControl<ListBox>("ResourceList");
        var label = this.FindControl<TextBlock>("CountLabel");
        if (list is null) return;

        var filtered = string.IsNullOrWhiteSpace(_filter)
            ? _allEntries
            : _allEntries.Where(e =>
                e.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                e.Category.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        list.ItemsSource = filtered;

        if (label != null)
            label.Text = $"{filtered.Count} / {_allEntries.Count}";
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _filter = (sender as TextBox)?.Text ?? string.Empty;
        ApplyFilter();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => Refresh();
}
