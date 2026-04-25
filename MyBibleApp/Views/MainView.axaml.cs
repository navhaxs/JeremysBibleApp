using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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
    private ToggleButton? _annotationToggle;
    private ToggleSwitch? _darkModeToggle;
    private ToggleButton? _splitViewToggle;
    private Button? _headerLookupButton;
    private bool _suppressSplitEvent;
    private bool _isApplyingLookupSelection;

    // Raised when the user taps the split-view toggle (true = split on, false = off).
    public event EventHandler<bool>? SplitToggled;

    private InkOverlayCanvas? _inkOverlay;
    private Border? _readerProgressTrack;
    private Border? _readerProgressThumb;
    private bool _isDraggingProgressBar;
    private ScrollViewer? _paragraphScrollViewer;
    private bool _isScrollTrackingAttached;
    private bool _waitingForLayoutToAttachScrollViewer;
    private IReadOnlyList<BibleParagraph> _paragraphs = [];
    // Saved scroll recognizers swapped out during annotation mode
    private readonly List<ScrollGestureRecognizer> _savedScrollRecognizers = new();

    // ── Annotation toolbar controls ──────────────────────────────────────────
    private Border? _annotationToolbar;
    private ToggleButton? _penModeButton;
    private ToggleButton? _highlighterModeButton;
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

    private bool _isMouseDragging;
    private Point _lastMousePosition;

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

            RefreshReaderProgress();
        };

        this.GetObservable(IsVisibleProperty).Subscribe(isVisible =>
        {
            // The secondary split pane starts hidden; wire scroll tracking once it is shown.
            if (isVisible)
                EnsureScrollTrackingAttached();

            RefreshReaderProgress();
        });
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _paragraphList  = this.FindControl<ListBox>("ParagraphList");
        _annotationToggle = this.FindControl<ToggleButton>("AnnotationToggle");
        _darkModeToggle   = this.FindControl<ToggleSwitch>("DarkModeToggle");
        _splitViewToggle  = this.FindControl<ToggleButton>("SplitViewToggle");
        _headerLookupButton = this.FindControl<Button>("HeaderLookupButton");
        _inkOverlay     = this.FindControl<InkOverlayCanvas>("InkOverlay");
        _readerProgressTrack = this.FindControl<Border>("ReaderProgressTrack");
        _readerProgressThumb = this.FindControl<Border>("ReaderProgressThumb");

        // ── Annotation toolbar controls ──────────────────────────────────────
        _annotationToolbar = this.FindControl<Border>("AnnotationToolbar");
        _penModeButton        = this.FindControl<ToggleButton>("PenModeButton");
        _highlighterModeButton = this.FindControl<ToggleButton>("HighlighterModeButton");
        _eraserModeButton     = this.FindControl<ToggleButton>("EraserModeButton");
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

        if (_readerProgressTrack != null)
            _readerProgressTrack.SizeChanged += (_, _) => RefreshReaderProgress();

        // ── Scroll offset tracking ───────────────────────────────────────────
        EnsureScrollTrackingAttached();
        RefreshReaderProgress();

        // ── Pen event routing → InkOverlay ──────────────────────────────────
        _paragraphList.AddHandler(PointerPressedEvent, OnListBoxPenPressed,
            handledEventsToo: true);
        
        // ── Mouse drag scrolling ─────────────────────────────────────────────
        _paragraphList.AddHandler(PointerPressedEvent, OnListBoxMousePressed,
            handledEventsToo: true);
        _paragraphList.AddHandler(PointerMovedEvent, OnListBoxMouseMoved,
            handledEventsToo: true);
        _paragraphList.AddHandler(PointerReleasedEvent, OnListBoxMouseReleased,
            handledEventsToo: true);

        _annotationToggle.IsCheckedChanged += OnAnnotationToggleChanged;
        UpdateAnnotationState();
    }

    private void EnsureScrollTrackingAttached()
    {
        if (_isScrollTrackingAttached || _paragraphList == null)
            return;

        var sv = _paragraphList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv == null)
        {
            // For hidden/unrealized panes, wait for layout updates instead of posting
            // recursive UI-thread callbacks that can starve startup.
            if (!_waitingForLayoutToAttachScrollViewer)
            {
                _paragraphList.LayoutUpdated += OnParagraphListLayoutUpdated;
                _waitingForLayoutToAttachScrollViewer = true;
            }
            return;
        }

        if (_waitingForLayoutToAttachScrollViewer)
        {
            _paragraphList.LayoutUpdated -= OnParagraphListLayoutUpdated;
            _waitingForLayoutToAttachScrollViewer = false;
        }

        _paragraphScrollViewer = sv;
        _paragraphScrollViewer.ScrollChanged += OnParagraphScrollChanged;
        _isScrollTrackingAttached = true;
    }

    private void OnParagraphListLayoutUpdated(object? sender, EventArgs e)
    {
        EnsureScrollTrackingAttached();
    }

    private void OnParagraphScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_paragraphScrollViewer == null)
            return;

        _inkOverlay?.UpdateScrollOffset(_paragraphScrollViewer.Offset.Y);
        UpdateReaderProgress(_paragraphScrollViewer);
    }

    private void RefreshReaderProgress()
    {
        EnsureScrollTrackingAttached();
        if (_paragraphScrollViewer != null)
            UpdateReaderProgress(_paragraphScrollViewer);
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
            if (_highlighterModeButton != null) _highlighterModeButton.IsChecked = false;
            if (_eraserModeButton != null) _eraserModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) { _inkOverlay.IsEraserMode = false; _inkOverlay.IsHighlighterMode = false; }
        }
        else
        {
            // Prevent un-checking pen unless another mode is taking over.
            if (_highlighterModeButton?.IsChecked != true && _eraserModeButton?.IsChecked != true)
            {
                _suppressToolbarUpdates = true;
                if (_penModeButton != null) _penModeButton.IsChecked = true;
                _suppressToolbarUpdates = false;
            }
        }
    }

    private void OnHighlighterModeIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressToolbarUpdates) return;

        if (_highlighterModeButton?.IsChecked == true)
        {
            _suppressToolbarUpdates = true;
            if (_penModeButton    != null) _penModeButton.IsChecked    = false;
            if (_eraserModeButton != null) _eraserModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) { _inkOverlay.IsEraserMode = false; _inkOverlay.IsHighlighterMode = true; }
        }
        else
        {
            // Prevent un-checking highlighter unless another mode is taking over.
            if (_penModeButton?.IsChecked != true && _eraserModeButton?.IsChecked != true)
            {
                _suppressToolbarUpdates = true;
                if (_highlighterModeButton != null) _highlighterModeButton.IsChecked = true;
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
            if (_penModeButton        != null) _penModeButton.IsChecked        = false;
            if (_highlighterModeButton != null) _highlighterModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) { _inkOverlay.IsEraserMode = true; _inkOverlay.IsHighlighterMode = false; }
        }
        else
        {
            // Prevent un-checking eraser unless another mode is taking over.
            if (_penModeButton?.IsChecked != true && _highlighterModeButton?.IsChecked != true)
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

        // Switch out of eraser mode when a colour is chosen; preserve pen/highlighter mode.
        if (_eraserModeButton?.IsChecked == true)
        {
            _suppressToolbarUpdates = true;
            if (_penModeButton    != null) _penModeButton.IsChecked    = true;
            if (_eraserModeButton != null) _eraserModeButton.IsChecked = false;
            if (_highlighterModeButton != null) _highlighterModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) { _inkOverlay.IsEraserMode = false; _inkOverlay.IsHighlighterMode = false; }
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

        // Switch out of eraser mode when a colour is chosen; preserve pen/highlighter mode.
        if (_eraserModeButton?.IsChecked == true)
        {
            _suppressToolbarUpdates = true;
            if (_penModeButton    != null) _penModeButton.IsChecked    = true;
            if (_eraserModeButton != null) _eraserModeButton.IsChecked = false;
            if (_highlighterModeButton != null) _highlighterModeButton.IsChecked = false;
            _suppressToolbarUpdates = false;
            if (_inkOverlay != null) { _inkOverlay.IsEraserMode = false; _inkOverlay.IsHighlighterMode = false; }
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
        if (_paragraphList == null || _paragraphs.Count == 0)
            return;

        var (topParagraph, topOffset) = GetTopVisibleParagraph();
        if (topParagraph == null)
            return;

        // Position the custom thumb by item-index fraction (accurate with virtualization).
        if (_readerProgressTrack != null && _readerProgressThumb != null && !_isDraggingProgressBar)
        {
            var paragraphIndex = FindParagraphIndex(topParagraph);
            if (paragraphIndex >= 0)
            {
                var totalLength   = Math.Max(1, _paragraphs.Count);
                var fraction      = Math.Clamp((paragraphIndex + topOffset) / totalLength, 0, 1);
                var trackHeight   = _readerProgressTrack.Bounds.Height;
                var thumbHeight   = _readerProgressThumb.Height;
                var maxTop        = Math.Max(0, trackHeight - thumbHeight);
                Canvas.SetTop(_readerProgressThumb, fraction * maxTop);
            }
        }

        if (DataContext is MainViewModel vm)
        {
            // Use the first visible body-text paragraph for verse sync so that
            // headings (which have no verse marker and inherit a stale StartVerse)
            // and the large chapter drop-cap whitespace don't skew the reported position.
            var syncParagraph = GetTopVisibleBodyTextParagraph() ?? topParagraph;
            vm.Header = $"{vm.BookTitle} {syncParagraph.StartChapter}:{syncParagraph.StartVerse}";
            vm.UpdateLookupFromReaderProgress(syncParagraph.StartChapter, syncParagraph.StartVerse);
        }
    }

    // ── Custom index-based scrollbar ─────────────────────────────────────────

    private void ScrollToFraction(double fraction)
    {
        if (_paragraphList == null || _paragraphs.Count == 0) return;
        fraction = Math.Clamp(fraction, 0, 1);
        var targetIndex = (int)(fraction * (_paragraphs.Count - 1));
        _paragraphList.ScrollIntoView(_paragraphs[targetIndex]);
    }

    private void OnProgressTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_readerProgressTrack == null) return;
        _isDraggingProgressBar = true;
        e.Pointer.Capture(_readerProgressTrack);
        var y = e.GetPosition(_readerProgressTrack).Y;
        ScrollToFraction(y / _readerProgressTrack.Bounds.Height);
        e.Handled = true;
    }

    private void OnProgressTrackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingProgressBar || _readerProgressTrack == null) return;
        var y = e.GetPosition(_readerProgressTrack).Y;
        ScrollToFraction(y / _readerProgressTrack.Bounds.Height);

        // Move thumb immediately for responsive feel while scroll catches up.
        if (_readerProgressThumb != null)
        {
            var trackHeight = _readerProgressTrack.Bounds.Height;
            var thumbHeight = _readerProgressThumb.Height;
            var maxTop      = Math.Max(0, trackHeight - thumbHeight);
            Canvas.SetTop(_readerProgressThumb, Math.Clamp(y - thumbHeight / 2, 0, maxTop));
        }
        e.Handled = true;
    }

    private void OnProgressTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingProgressBar) return;
        _isDraggingProgressBar = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private async void OnLookupGoButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingLookupSelection) return;

        if (DataContext is not MainViewModel vm) return;
        if (vm.SelectedLookupBook == null) return;
        if (vm.SelectedLookupChapter < 1 || vm.SelectedLookupVerse < 1) return;

        _isApplyingLookupSelection = true;
        try
        {
            var requestedCode = vm.SelectedLookupBook.Code;
            var requestedName = vm.SelectedLookupBook.Name;
            var requestedChapter = vm.SelectedLookupChapter;
            var requestedVerse = vm.SelectedLookupVerse;

            if (!requestedCode.Equals(vm.BookCode, StringComparison.OrdinalIgnoreCase))
            {
                var result = await vm.TryLoadBookFromApiAsync(requestedCode, requestedChapter, requestedVerse);
                if (!result.Success)
                    vm.Status = $"Could not load {requestedName} online: {result.Error}";
            }

            _paragraphs = vm.Paragraphs;
            vm.Header = $"{vm.BookTitle} {vm.SelectedLookupChapter}:{vm.SelectedLookupVerse}";

            if (requestedCode.Equals(vm.BookCode, StringComparison.OrdinalIgnoreCase))
                await ScrollToReferenceAsync(requestedChapter, requestedVerse);

            if (_headerLookupButton?.Flyout is Flyout flyout)
                flyout.Hide();
        }
        finally
        {
            _isApplyingLookupSelection = false;
        }
    }


    private async Task ScrollToReferenceAsync(int chapter, int verse)
    {
        if (_paragraphList == null || _paragraphs.Count == 0) return;

        // Prefer body-text paragraphs. Headings inherit a stale StartVerse from the
        // previous body paragraph and don't contain verse content, so landing on them
        // would place the user above the intended verse.
        BibleParagraph? target = null;

        if (verse == 1)
        {
            // Scroll to the very start of the chapter — the heading if one exists,
            // otherwise the drop-cap paragraph — so the big chapter number and any
            // chapter heading are visible at the top.
            target = _paragraphs.FirstOrDefault(p => p.StartChapter == chapter);
        }

        target ??= _paragraphs.LastOrDefault(p =>
            p.IsBodyText &&
            (p.StartChapter < chapter || (p.StartChapter == chapter && p.StartVerse <= verse)));

        // Fallback: include non-body paragraphs if no body text matched.
        target ??= _paragraphs.LastOrDefault(p =>
            p.StartChapter < chapter || (p.StartChapter == chapter && p.StartVerse <= verse));

        target ??= _paragraphs.FirstOrDefault(p =>
            p.StartChapter > chapter || (p.StartChapter == chapter && p.StartVerse >= verse));

        target ??= _paragraphs.LastOrDefault();
        if (target == null)
            return;

        // Retry loop: re-issue ScrollIntoView on each attempt so that it fires after
        // the ListBox has applied a newly-changed ItemsSource (book navigation) and
        // after the virtualized container is realized in the visual tree.
        ListBoxItem? item = null;
        for (var attempt = 0; attempt < 5 && item == null; attempt++)
        {
            _paragraphList.ScrollIntoView(target);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            item = _paragraphList.GetVisualDescendants()
                .OfType<ListBoxItem>()
                .FirstOrDefault(x => ReferenceEquals(x.DataContext, target));
        }

        if (item == null)
            return;

        EnsureScrollTrackingAttached();
        if (_paragraphScrollViewer == null)
            return;

        var itemTopInViewport = item.TranslatePoint(default, _paragraphScrollViewer)?.Y;
        if (!itemTopInViewport.HasValue)
            return;

        var desiredY = _paragraphScrollViewer.Offset.Y + itemTopInViewport.Value;
        var maxY = Math.Max(0, _paragraphScrollViewer.Extent.Height - _paragraphScrollViewer.Viewport.Height);
        desiredY = Math.Clamp(desiredY, 0, maxY);

        _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, desiredY);
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

    /// <summary>
    /// Returns the topmost visible body-text paragraph for chapter/verse syncing.
    /// Skips headings and parallel references (their <see cref="BibleParagraph.StartVerse"/>
    /// is not updated by verse markers and may reflect a stale position).
    /// Also skips the topmost item when it is mostly scrolled off AND is a chapter
    /// drop-cap or heading, so that the large visual gap at the top of a chapter
    /// doesn't prevent the next paragraph from becoming the reported position.
    /// </summary>
    private BibleParagraph? GetTopVisibleBodyTextParagraph()
    {
        if (_paragraphList == null)
            return null;

        var candidates = _paragraphList.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Select(item => new
            {
                Top = item.TranslatePoint(default, _paragraphList)?.Y,
                Height = item.Bounds.Height,
                Paragraph = item.DataContext as BibleParagraph
            })
            .Where(x => x.Paragraph != null && x.Top.HasValue && x.Height > 0)
            .Select(x => new
            {
                Paragraph = x.Paragraph!,
                Top = x.Top!.Value,
                Height = x.Height
            })
            .Where(x => x.Top + x.Height > 0)
            .OrderBy(x => x.Top)
            .ToList();

        if (candidates.Count == 0)
            return null;

        foreach (var candidate in candidates)
        {
            if (!candidate.Paragraph.IsBodyText)
                continue;

            // If this item is mostly scrolled off (>85 % hidden) AND it carries a
            // chapter drop cap or the item after it is also body text, skip it so
            // the heading/drop-cap whitespace at the top doesn't anchor the verse.
            var visibleFraction = Math.Clamp((candidate.Top + candidate.Height) / candidate.Height, 0.0, 1.0);
            if (visibleFraction < 0.15 && candidate.Paragraph.HasChapterDropCap)
                continue;

            return candidate.Paragraph;
        }

        // Fallback: return the first body-text paragraph found, even if it's mostly off-screen.
        return candidates.FirstOrDefault(x => x.Paragraph.IsBodyText)?.Paragraph;
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

    private async void OnSyncAuthButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.AuthenticateAsync();
    }

    private void OnSyncSignOutButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SignOut();
    }

    private void OnSyncForceSyncButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ForceSync();
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

    // ── Mouse drag scrolling ─────────────────────────────────────────────────

    private void OnListBoxMousePressed(object? sender, PointerPressedEventArgs e)
    {
        // Only handle mouse left or middle button for drag scrolling
        if (e.Pointer.Type != PointerType.Mouse) return;

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed &&
            properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed)
            return;

        // Don't intercept clicks on the scrollbar or other interactive controls
        if (e.Source is Visual sourceVisual &&
            (sourceVisual.FindAncestorOfType<ScrollBar>(includeSelf: true) != null ||
             sourceVisual.FindAncestorOfType<Button>(includeSelf: true) != null))
            return;

        _isMouseDragging = true;
        _lastMousePosition = e.GetPosition(this);
        e.Pointer.Capture(_paragraphList);
        e.Handled = true;
    }

    private void OnListBoxMouseMoved(object? sender, PointerEventArgs e)
    {
        if (!_isMouseDragging || _paragraphScrollViewer == null) return;
        if (e.Pointer.Type != PointerType.Mouse) return;

        var currentPosition = e.GetPosition(this);
        var deltaY = _lastMousePosition.Y - currentPosition.Y;

        // Apply scroll with smooth movement
        var newOffset = _paragraphScrollViewer.Offset.Y + deltaY;
        var maxY = Math.Max(0, _paragraphScrollViewer.Extent.Height - _paragraphScrollViewer.Viewport.Height);
        newOffset = Math.Clamp(newOffset, 0, maxY);

        _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, newOffset);
        _lastMousePosition = currentPosition;
        
        e.Handled = true;
    }

    private void OnListBoxMouseReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isMouseDragging || e.Pointer.Type != PointerType.Mouse) return;

        _isMouseDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}    // ...existing code...


