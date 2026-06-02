using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyBibleApp.Services;

namespace MyBibleApp.Controls;

/// <summary>
/// A <see cref="WrapPanel"/> that renders a USX parallel-reference string
/// (e.g. "(Psalms 75:1–10)") as a mix of plain italic text and tappable
/// hyperlink-style buttons. Raises <see cref="ReferenceClickedEvent"/> —
/// a bubbling routed event — when the user taps a reference.
/// </summary>
public class CrossReferenceBlock : WrapPanel
{
    // ── Styled property ──────────────────────────────────────────────────────

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CrossReferenceBlock, string?>(nameof(Text));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // ── Routed event ─────────────────────────────────────────────────────────

    /// <summary>
    /// Bubbling routed event fired when the user taps a recognised reference.
    /// Subscribe at any ancestor (e.g. the ListBox) to handle navigation.
    /// </summary>
    public static readonly RoutedEvent<CrossRefClickedEventArgs> ReferenceClickedEvent =
        RoutedEvent.Register<CrossReferenceBlock, CrossRefClickedEventArgs>(
            nameof(ReferenceClicked), RoutingStrategies.Bubble);

    public event EventHandler<CrossRefClickedEventArgs> ReferenceClicked
    {
        add    => AddHandler(ReferenceClickedEvent, value);
        remove => RemoveHandler(ReferenceClickedEvent, value);
    }

    // ── Rebuild on Text change ────────────────────────────────────────────────

    static CrossReferenceBlock()
    {
        TextProperty.Changed.AddClassHandler<CrossReferenceBlock>((b, _) => b.Rebuild());
    }

    private void Rebuild()
    {
        Children.Clear();
        var text = Text;
        if (string.IsNullOrEmpty(text)) return;

        foreach (var segment in CrossReferenceParser.Parse(text))
        {
            if (segment is TextSegment ts)
            {
                Children.Add(new TextBlock
                {
                    Text              = ts.Text,
                    FontStyle         = FontStyle.Italic,
                    FontSize          = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0),
                });
            }
            else if (segment is ReferenceSegment rs)
            {
                var capturedRs = rs;
                var linkText = new TextBlock
                {
                    Text             = rs.Display,
                    FontStyle        = FontStyle.Italic,
                    FontSize         = 15,
                    TextDecorations  = TextDecorations.Underline,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var btn = new Button
                {
                    Content         = linkText,
                    Padding         = new Thickness(0),
                    Margin          = new Thickness(0),
                    Background      = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor          = new Cursor(StandardCursorType.Hand),
                    VerticalAlignment = VerticalAlignment.Center,
                    Classes         = { "cross-ref-link" },
                };
                btn.Click += (_, _) =>
                {
                    var args = new CrossRefClickedEventArgs(
                        ReferenceClickedEvent,
                        capturedRs.BookCode,
                        capturedRs.Chapter,
                        capturedRs.Verse);
                    RaiseEvent(args);
                };
                Children.Add(btn);
            }
        }
    }
}

/// <summary>Event args carried by <see cref="CrossReferenceBlock.ReferenceClickedEvent"/>.</summary>
public sealed class CrossRefClickedEventArgs : RoutedEventArgs
{
    public CrossRefClickedEventArgs(
        RoutedEvent routedEvent, string bookCode, int chapter, int verse)
        : base(routedEvent)
    {
        BookCode = bookCode;
        Chapter  = chapter;
        Verse    = verse;
    }

    /// <summary>3-char book code matching books.json (e.g. "psa", "gen").</summary>
    public string BookCode { get; }
    public int    Chapter  { get; }
    public int    Verse    { get; }
}
