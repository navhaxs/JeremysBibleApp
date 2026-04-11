using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MyBibleApp.Controls;
using MyBibleApp.Models;

namespace MyBibleApp.Views;

public partial class MainView : UserControl
{
    private readonly Flyout _footnoteFlyout;
    private readonly SelectableTextBlock _footnoteTextBlock;
    private ListBox? _paragraphList;
    private ToggleSwitch? _annotationToggle;
    private ToggleSwitch? _darkModeToggle;
    private InkOverlayCanvas? _inkOverlay;
    private Border? _readerProgressTrack;
    private Avalonia.Controls.Shapes.Rectangle? _readerProgressFill;
    private IReadOnlyList<BibleParagraph> _paragraphs = [];
    // Saved scroll recognizers swapped out during annotation mode
    private readonly List<ScrollGestureRecognizer> _savedScrollRecognizers = new();

    public MainView()
    {
        InitializeComponent();

        _footnoteTextBlock = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        _footnoteFlyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = new Border { MaxWidth = 420, Padding = new Thickness(8), Child = _footnoteTextBlock }
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _paragraphList  = this.FindControl<ListBox>("ParagraphList");
        _annotationToggle = this.FindControl<ToggleSwitch>("AnnotationToggle");
        _darkModeToggle = this.FindControl<ToggleSwitch>("DarkModeToggle");
        _inkOverlay     = this.FindControl<InkOverlayCanvas>("InkOverlay");
        _readerProgressTrack = this.FindControl<Border>("ReaderProgressTrack");
        _readerProgressFill  = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("ReaderProgressFill");

        // Initialise the dark-mode toggle to reflect the current theme.
        if (_darkModeToggle != null)
            _darkModeToggle.IsChecked = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        if (DataContext is MyBibleApp.ViewModels.MainViewModel vm)
        {
            _paragraphs = vm.Paragraphs;
        }

        if (_paragraphList == null || _annotationToggle == null) return;

        // ── Scroll offset tracking ───────────────────────────────────────────
        // Keep the InkOverlay in sync with the ListBox scroll so strokes
        // appear anchored to the text content.
        var sv = _paragraphList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv != null)
        {
            sv.ScrollChanged += (_, _) =>
            {
                if (_inkOverlay != null)
                {
                    _inkOverlay.UpdateScrollOffset(sv.Offset.Y);
                }

                UpdateReaderProgress(sv);
            };

            UpdateReaderProgress(sv);

        // ── Reader progress tracking ─────────────────────────────────────────────
        // Update the vertical progress bar as reader scrolls through the book.
        }

        // ── Pen event routing → InkOverlay ──────────────────────────────────
        // When annotation is on and a PEN presses, capture the pointer to the
        // InkOverlay so all subsequent Moved/Released events go directly to it.
        // We listen in the bubble phase with handledEventsToo so we always fire.
        _paragraphList.AddHandler(PointerPressedEvent, OnListBoxPenPressed,
            handledEventsToo: true);

        _annotationToggle.IsCheckedChanged += OnAnnotationToggleChanged;
        UpdateAnnotationState();
    }

    // ── Pen press routing ────────────────────────────────────────────────────

    private void OnListBoxPenPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_annotationToggle?.IsChecked != true) return;
        if (e.Pointer.Type != PointerType.Pen) return;
        if (_inkOverlay == null) return;

        // Start a stroke at the pen's viewport position relative to the overlay.
        var pos = e.GetPosition(_inkOverlay);
        _inkOverlay.StartStroke(pos);

        // Capture the pointer to the overlay.
        // All subsequent PointerMoved / PointerReleased events now go directly
        // to InkOverlay.OnPointerMoved / OnPointerReleased, bypassing the ListBox.
        e.Pointer.Capture(_inkOverlay);
        e.Handled = true;

    }

    // ── Annotation toggle / scroll gesture swap ───────────────────────────────

    private void OnAnnotationToggleChanged(object? sender, RoutedEventArgs e)
    {
        UpdateAnnotationState();
    }

    private void UpdateAnnotationState()
    {
        if (_paragraphList == null) return;
        bool isAnnotating = _annotationToggle?.IsChecked == true;

        var scp = _paragraphList.GetVisualDescendants()
            .OfType<ScrollContentPresenter>().FirstOrDefault();
        if (scp == null) return;

        if (isAnnotating && _savedScrollRecognizers.Count == 0)
        {
            // Swap out the default ScrollGestureRecognizer for a touch-only version
            // so the recognizer never competes with pen input.
            var existing = scp.GestureRecognizers
                .OfType<ScrollGestureRecognizer>().ToList();
            foreach (var r in existing)
            {
                _savedScrollRecognizers.Add(r);
                scp.GestureRecognizers.Remove(r);
            }
            if (_savedScrollRecognizers.Count > 0)
            {
                var orig = _savedScrollRecognizers[0];
                scp.GestureRecognizers.Add(new TouchOnlyScrollGestureRecognizer
                {
                    CanHorizontallyScroll  = orig.CanHorizontallyScroll,
                    CanVerticallyScroll    = orig.CanVerticallyScroll,
                    IsScrollInertiaEnabled = orig.IsScrollInertiaEnabled
                });
            }
        }
        else if (!isAnnotating && _savedScrollRecognizers.Count > 0)
        {
            foreach (var r in scp.GestureRecognizers
                         .OfType<TouchOnlyScrollGestureRecognizer>().ToList())
                scp.GestureRecognizers.Remove(r);
            foreach (var r in _savedScrollRecognizers)
                scp.GestureRecognizers.Add(r);
            _savedScrollRecognizers.Clear();
        }
    }

    private void UpdateReaderProgress(ScrollViewer scrollViewer)
    {
        if (_readerProgressTrack == null || _readerProgressFill == null)
            return;

        if (_paragraphList == null || _paragraphs.Count == 0)
        {
            _readerProgressFill.Height = 0;
            return;
        }

        var (topParagraph, topOffset) = GetTopVisibleParagraph();
        if (topParagraph == null)
            return;

        var paragraphIndex = FindParagraphIndex(topParagraph);
        if (paragraphIndex < 0)
            return;

        var totalLength = Math.Max(1, _paragraphs.Count);
        var bookOffset  = Math.Clamp(paragraphIndex + topOffset, 0, totalLength);
        var fraction    = bookOffset / totalLength;

        _readerProgressFill.Height = _readerProgressTrack.Bounds.Height * fraction;

        if (DataContext is MyBibleApp.ViewModels.MainViewModel vm)
        {
            vm.Header = $"{vm.BookTitle} {topParagraph.StartChapter}:{topParagraph.StartVerse}";
        }
    }

    private (BibleParagraph? Paragraph, double OffsetWithinParagraph) GetTopVisibleParagraph()
    {
        if (_paragraphList == null)
        {
            return (null, 0);
        }

        var candidates = _paragraphList.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Select(item => new
            {
                Item = item,
                Top = item.TranslatePoint(default, _paragraphList)?.Y,
                Height = item.Bounds.Height,
                Paragraph = item.DataContext as BibleParagraph
            })
            .Where(x => x.Paragraph != null && x.Top.HasValue && x.Height > 0)
            .Select(x => new
            {
                x.Paragraph,
                Top = x.Top!.Value,
                x.Height
            })
            .Where(x => x.Top + x.Height > 0)
            .OrderBy(x => x.Top)
            .ToList();

        if (candidates.Count == 0)
        {
            return (null, 0);
        }

        var top = candidates[0];
        var offsetWithinParagraph = Math.Clamp(-top.Top / top.Height, 0, 1);
        return (top.Paragraph, offsetWithinParagraph);
    }

    private int FindParagraphIndex(BibleParagraph paragraph)
    {
        for (var i = 0; i < _paragraphs.Count; i++)
        {
            if (ReferenceEquals(_paragraphs[i], paragraph))
            {
                return i;
            }
        }

        return -1;
    }


    // ── Settings flyout handlers ─────────────────────────────────────────────

    private void OnDarkModeToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || Application.Current == null) return;
        Application.Current.RequestedThemeVariant =
            toggle.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    // ── Standard handlers ────────────────────────────────────────────────────

    private void OnFootnoteButtonClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not BibleFootnote footnote) return;
        _footnoteTextBlock.Text = footnote.Text;
        _footnoteFlyout.ShowAt(button);
    }

    private void OnParagraphListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedIndex != -1)
            listBox.SelectedIndex = -1;
    }
}