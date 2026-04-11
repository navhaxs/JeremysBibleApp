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
using MyBibleApp.ViewModels;
using Color = Avalonia.Media.Color;

namespace MyBibleApp.Views;

public partial class MainView : UserControl
{
    private readonly Flyout _footnoteFlyout;
    private readonly SelectableTextBlock _footnoteTextBlock;
    private ListBox? _paragraphList;
    private ToggleSwitch? _annotationToggle;
    private ToggleSwitch? _darkModeToggle;
    private ToggleButton? _splitViewToggle;
    private Button? _headerLookupButton;
    private bool _suppressSplitEvent;

    // Raised when the user taps the split-view toggle (true = split on, false = off).
    public event EventHandler<bool>? SplitToggled;

    private InkOverlayCanvas? _inkOverlay;
    private Border? _readerProgressTrack;
    private Avalonia.Controls.Shapes.Rectangle? _readerProgressFill;
    private IReadOnlyList<BibleParagraph> _paragraphs = [];
    // Saved scroll recognizers swapped out during annotation mode
    private readonly List<ScrollGestureRecognizer> _savedScrollRecognizers = new();

    // ── Annotation toolbar controls ──────────────────────────────────────────
    private Border? _annotationToolbar;
    private ToggleButton? _penModeButton;
    private ToggleButton? _eraserModeButton;
    private Button? _colorAmber;
    private Button? _colorRed;
    private Button? _colorBlue;
    private Button? _colorDark;
    private Button? _customColorButton;
    private Button? _undoButton;
    private ColorView? _colorPickerView;
    private Button? _activeColorSwatch;
    private bool _suppressToolbarUpdates;

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

        // Keep _paragraphs in sync when DataContext is set after Loaded fires
        // (e.g. the secondary split pane gets its VM assigned lazily).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MyBibleApp.ViewModels.MainViewModel vm)
                _paragraphs = vm.Paragraphs;
        };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _paragraphList  = this.FindControl<ListBox>("ParagraphList");
        _annotationToggle = this.FindControl<ToggleSwitch>("AnnotationToggle");
        _darkModeToggle   = this.FindControl<ToggleSwitch>("DarkModeToggle");
        _splitViewToggle  = this.FindControl<ToggleButton>("SplitViewToggle");
        _headerLookupButton = this.FindControl<Button>("HeaderLookupButton");
        _inkOverlay     = this.FindControl<InkOverlayCanvas>("InkOverlay");
        _readerProgressTrack = this.FindControl<Border>("ReaderProgressTrack");
        _readerProgressFill  = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("ReaderProgressFill");

        // ── Annotation toolbar controls ──────────────────────────────────────
        _annotationToolbar = this.FindControl<Border>("AnnotationToolbar");
        _penModeButton     = this.FindControl<ToggleButton>("PenModeButton");
        _eraserModeButton  = this.FindControl<ToggleButton>("EraserModeButton");
        _colorAmber        = this.FindControl<Button>("ColorAmber");
        _colorRed          = this.FindControl<Button>("ColorRed");
        _colorBlue         = this.FindControl<Button>("ColorBlue");
        _colorDark         = this.FindControl<Button>("ColorDark");
        _customColorButton = this.FindControl<Button>("CustomColorButton");
        _undoButton        = this.FindControl<Button>("UndoButton");

        // Build the ColorView flyout for the custom-colour button.
        _colorPickerView = new ColorView
        {
            Color          = Color.FromArgb(0xD4, 0xFF, 0xC1, 0x07),
            IsAlphaEnabled = true
        };
        _colorPickerView.ColorChanged += OnColorPickerColorChanged;
        if (_customColorButton != null)
        {
            _customColorButton.Flyout = new Flyout
            {
                Placement = PlacementMode.Top,
                Content   = _colorPickerView
            };
        }

        // Set initial active colour swatch (amber).
        SetActiveColorSwatch(_colorAmber);

        // Initialise the dark-mode toggle to reflect the current theme.
        if (_darkModeToggle != null)
            _darkModeToggle.IsChecked = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        if (DataContext is MyBibleApp.ViewModels.MainViewModel vm)
        {
            _paragraphs = vm.Paragraphs;
        }

        if (_paragraphList == null || _annotationToggle == null) return;

        // ── Scroll offset tracking ───────────────────────────────────────────
        var sv = _paragraphList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv != null)
        {
            sv.ScrollChanged += (_, _) =>
            {
                if (_inkOverlay != null)
                    _inkOverlay.UpdateScrollOffset(sv.Offset.Y);

                UpdateReaderProgress(sv);
            };

            UpdateReaderProgress(sv);
        }

        // ── Pen event routing → InkOverlay ──────────────────────────────────
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

    // ── Split-view toggle ────────────────────────────────────────────────────

    private void OnSplitViewToggleIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressSplitEvent) return;
        SplitToggled?.Invoke(this, _splitViewToggle?.IsChecked == true);
    }

    /// <summary>Called by AppShellView to sync the button state without re-firing the event.</summary>
    public void SetSplitActive(bool isActive)
    {
        _suppressSplitEvent = true;
        if (_splitViewToggle != null) _splitViewToggle.IsChecked = isActive;
        _suppressSplitEvent = false;
    }

    private void UpdateAnnotationState()
    {
        if (_paragraphList == null) return;
        bool isAnnotating = _annotationToggle?.IsChecked == true;

        // Show / hide the floating toolbar.
        if (_annotationToolbar != null)
            _annotationToolbar.IsVisible = isAnnotating;

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

    // ── Toolbar: pen / eraser mode ────────────────────────────────────────────

    private void OnPenModeIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressToolbarUpdates) return;

        if (_penModeButton?.IsChecked == true)
        {
            _suppressToolbarUpdates = true;
            if (_eraserModeButton != null) _eraserModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) _inkOverlay.IsEraserMode = false;
        }
        else
        {
            // Prevent un-checking pen unless eraser is the one taking over.
            if (_eraserModeButton?.IsChecked != true)
            {
                _suppressToolbarUpdates = true;
                if (_penModeButton != null) _penModeButton.IsChecked = true;
                _suppressToolbarUpdates = false;
            }
        }
    }

    private void OnEraserModeIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressToolbarUpdates) return;

        if (_eraserModeButton?.IsChecked == true)
        {
            _suppressToolbarUpdates = true;
            if (_penModeButton != null) _penModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) _inkOverlay.IsEraserMode = true;
        }
        else
        {
            // Prevent un-checking eraser unless pen is taking over.
            if (_penModeButton?.IsChecked != true)
            {
                _suppressToolbarUpdates = true;
                if (_eraserModeButton != null) _eraserModeButton.IsChecked = true;
                _suppressToolbarUpdates = false;
            }
        }
    }

    // ── Toolbar: undo ─────────────────────────────────────────────────────────

    private void OnUndoButtonClick(object? sender, RoutedEventArgs e)
    {
        _inkOverlay?.UndoStroke();
    }

    // ── Toolbar: colour selection ─────────────────────────────────────────────

    private void OnColorSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Background is not ISolidColorBrush brush) return;

        ApplyColor(brush.Color);
        SetActiveColorSwatch(button);

        // Reset the custom-colour button back to its default appearance.
        if (_customColorButton != null)
            _customColorButton.ClearValue(Button.BackgroundProperty);

        // Sync ColorView so it reflects the chosen preset.
        if (_colorPickerView != null) _colorPickerView.Color = brush.Color;

        // Switch back to pen mode when a colour is chosen.
        if (_penModeButton?.IsChecked != true)
        {
            _suppressToolbarUpdates = true;
            if (_penModeButton    != null) _penModeButton.IsChecked    = true;
            if (_eraserModeButton != null) _eraserModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) _inkOverlay.IsEraserMode = false;
        }
    }

    private void OnColorPickerColorChanged(object? sender, ColorChangedEventArgs e)
    {
        ApplyColor(e.NewColor);

        // Tint the custom-colour button to show the chosen colour.
        if (_customColorButton != null)
            _customColorButton.Background = new SolidColorBrush(e.NewColor);

        // Deselect preset swatches – a custom colour is now active.
        SetActiveColorSwatch(null);

        // Switch back to pen mode when a colour is chosen.
        if (_penModeButton?.IsChecked != true)
        {
            _suppressToolbarUpdates = true;
            if (_penModeButton    != null) _penModeButton.IsChecked    = true;
            if (_eraserModeButton != null) _eraserModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) _inkOverlay.IsEraserMode = false;
        }
    }

    private void ApplyColor(Color color)
    {
        if (_inkOverlay != null) _inkOverlay.PenColor = color;
    }

    private void SetActiveColorSwatch(Button? button)
    {
        _activeColorSwatch?.Classes.Remove("selected");
        _activeColorSwatch = button;
        _activeColorSwatch?.Classes.Add("selected");
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

        if (DataContext is MainViewModel vm)
        {
            vm.Header = $"{vm.BookTitle} {topParagraph.StartChapter}:{topParagraph.StartVerse}";
            vm.SelectedLookupChapter = topParagraph.StartChapter;
            vm.SelectedLookupVerse = topParagraph.StartVerse;
        }
    }

    private void OnLookupVerseSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.SelectedLookupBook == null) return;
        if (vm.SelectedLookupChapter < 1 || vm.SelectedLookupVerse < 1) return;

        var selectedBook = vm.SelectedLookupBook;
        var displayTitle = selectedBook.Code.Equals(vm.BookCode, StringComparison.OrdinalIgnoreCase)
            ? vm.BookTitle
            : selectedBook.Name;

        vm.Header = $"{displayTitle} {vm.SelectedLookupChapter}:{vm.SelectedLookupVerse}";

        // We only have one local USX sample loaded right now, so scroll only when
        // the picked reference is in the currently loaded book.
        if (selectedBook.Code.Equals(vm.BookCode, StringComparison.OrdinalIgnoreCase))
            ScrollToReference(vm.SelectedLookupChapter, vm.SelectedLookupVerse);

        if (_headerLookupButton?.Flyout is Flyout flyout)
            flyout.Hide();
    }

    private void ScrollToReference(int chapter, int verse)
    {
        if (_paragraphList == null || _paragraphs.Count == 0) return;

        var target = _paragraphs.FirstOrDefault(p =>
            p.StartChapter > chapter || (p.StartChapter == chapter && p.StartVerse >= verse));

        target ??= _paragraphs.LastOrDefault();
        if (target != null)
            _paragraphList.ScrollIntoView(target);
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