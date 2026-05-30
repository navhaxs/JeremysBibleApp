using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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
using MyBibleApp.Helpers;
using MyBibleApp.Models;
using MyBibleApp.Services;
using MyBibleApp.ViewModels;
using Color = Avalonia.Media.Color;

namespace MyBibleApp.Views;

public partial class MainView : UserControl
{
    private readonly Flyout _footnoteFlyout;
    private readonly SelectableTextBlock _footnoteTextBlock;
    private ListBox? _paragraphList;
    private ToggleButton? _annotationToggle;
    private StackPanel? _themeSwatchPanel;
    private ToggleButton? _splitViewToggle;
    private Button? _headerLookupButton;
    private bool _suppressSplitEvent;
    private bool _isApplyingLookupSelection;

    // Windowed paragraph loading.
    private readonly ObservableCollection<BibleParagraph>
        _windowedItems = [];
    private int _windowStart;   // index into _chapterGroups (0-based)
    private int _windowEnd;     // exclusive upper bound into _chapterGroups

    // Events for chapter enter/exit — AppShellView uses these to load/unload ink strokes.
    public event EventHandler<int>? ChapterEnteredWindow;
    public event EventHandler<int>? ChapterExitedWindow;

    public int WindowStart => _windowStart;   // 0-based index into _chapterGroups
    public int WindowEnd   => _windowEnd;     // exclusive

    /// <summary>Book code of the currently loaded book, for ink store queries.</summary>
    public string CurrentBookCode =>
        (DataContext as ScriptureViewModel)?.BookCode ?? string.Empty;

    // Raised when the user taps the split-view toggle (true = split on, false = off).
    public event EventHandler<bool>? SplitToggled;

    // Raised when the user taps the My Bible Reading button.
    public event EventHandler? BibleReadingRequested;

    // Raised when the user taps the Journals button.
    public event EventHandler? JournalsRequested;

    // Surfaces ink events from the overlay canvas.
    public event EventHandler<InkStrokeEventArgs>? StrokeCompleted;
    public event EventHandler<InkStrokeRemovedEventArgs>? StrokeRemoved;

    private InkOverlayCanvas? _inkOverlay;
    private InkOverlayCanvas? _penUnderlay;
    private Grid? _inkAreaGrid;
    private Border? _readerProgressTrack;
    private Border? _readerProgressThumb;
    private Canvas? _chapterMarkersCanvas;
    private bool _isDraggingProgressBar;
    private CancellationTokenSource? _scrollbarHideCts;
    private ScrollViewer? _paragraphScrollViewer;
    private bool _isScrollTrackingAttached;
    private bool _waitingForLayoutToAttachScrollViewer;
    private IReadOnlyList<BibleParagraph> _paragraphs = [];
    // Chapter grouping built from _paragraphs on every book load.
    // _chapterGroups[i] = all paragraphs for chapter (i+1), in order.
    private List<List<BibleParagraph>> _chapterGroups = [];
    // Fast lookup: paragraph → (1-based chapter, within-chapter index).
    private Dictionary<BibleParagraph, (int Chapter, int LocalIndex)> _paragraphChapterInfo = [];

    // Chapter content positions — populated from visual tree when chapters are realized.
    private Dictionary<int, double> _chapterStartY = [];
    // Per-chapter within-chapter local tops. Only populated when chapter is in window.
    private Dictionary<int, double[]> _chapterLocalTops = [];
    private ScriptureViewModel? _subscribedVm;
    // Velocity-based chapter marker reveal
    private double _lastScrollOffset;
    private DateTime _lastScrollTime;
    private int _fastScrollCount;
    private bool _chapterMarkersShownByScroll;
    private CancellationTokenSource? _scrollStopCts;
    private const double ScrollVelocityThreshold = 3000; // pixels per second
    private const int FastScrollCountThreshold = 3;      // consecutive fast events required
    // Saved scroll recognizers swapped out during annotation mode
    private readonly List<ScrollGestureRecognizer> _savedScrollRecognizers = new();

    // ── Annotation toolbar controls ──────────────────────────────────────────
    private Border? _annotationSection;
    private ToggleButton? _penModeButton;
    private ToggleButton? _highlighterModeButton;
    private ToggleButton? _eraserModeButton;
    private Button? _colorAmber;
    private Button? _colorRed;
    private Button? _colorBlue;
    private Button? _colorDark;
    private Button? _customColorButton;
    private Button? _undoButton;
    private Button? _redoButton;
    private Button? _journalsHeaderButton;
    private Border? _journalUnsavedBadge;
    private TextBlock? _activeJournalLabel;
    private ColorView? _colorPickerView;
    private Button? _activeColorSwatch;
    private bool _suppressToolbarUpdates;

    private bool _isMouseDragging;
    private Point _lastMousePosition;
    private bool _isTouchPanning;
    private Point _lastTouchPosition;
    private bool _suppressReaderProgressSync;
    private bool _suppressScrollEventsForTabSwitch;
    private double? _pendingScrollRestoreY;
    private int _scrollRestoreRetries;
    private bool _isAdjustingWindow;
    private bool _windowCheckPending;

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
            if (_subscribedVm != null)
            {
                _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
                _subscribedVm = null;
            }

            if (DataContext is MyBibleApp.ViewModels.ScriptureViewModel vm)
            {
                _paragraphs = vm.Paragraphs;
                _subscribedVm = vm;
                vm.PropertyChanged += OnVmPropertyChanged;
            }

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
        if (_paragraphList != null)
            _paragraphList.ItemsSource = _windowedItems;
        _annotationToggle = this.FindControl<ToggleButton>("AnnotationToggle");
        _themeSwatchPanel  = this.FindControl<StackPanel>("ThemeSwatchPanel");
        _splitViewToggle  = this.FindControl<ToggleButton>("SplitViewToggle");
        _headerLookupButton = this.FindControl<Button>("HeaderLookupButton");
        _inkOverlay     = this.FindControl<InkOverlayCanvas>("InkOverlay");
        _penUnderlay    = this.FindControl<InkOverlayCanvas>("PenUnderlay");
        _inkAreaGrid    = this.FindControl<Grid>("InkAreaGrid");

        // Provide paragraph-position callbacks so ink strokes can anchor to
        // paragraphs and survive virtualizing-panel re-layout.
        if (_inkOverlay != null)
        {
            _inkOverlay.FindParagraphAtContentY = FindParagraphAtContentY;
            _inkOverlay.GetParagraphContentTop = GetParagraphContentTopFast;
            _inkOverlay.StrokeCompleted += (_, e) => StrokeCompleted?.Invoke(this, e);
            _inkOverlay.StrokeRemoved += (_, e) => StrokeRemoved?.Invoke(this, e);
            // Highlights stay on the overlay (above text, Multiply blend).
            _inkOverlay.DrawMode = InkDrawMode.HighlightOnly;
        }

        // Pen underlay sits below the text layer; it mirrors stroke data from the overlay.
        if (_penUnderlay != null && _inkOverlay != null)
        {
            _penUnderlay.DataSource = _inkOverlay;
            _penUnderlay.DrawMode   = InkDrawMode.PenOnly;
            _inkOverlay.RegisterSlave(_penUnderlay);
        }

        _journalsHeaderButton = this.FindControl<Button>("JournalsButton");
        _journalUnsavedBadge = this.FindControl<Border>("JournalUnsavedBadge");
        _activeJournalLabel = this.FindControl<TextBlock>("ActiveJournalLabel");
        _redoButton = this.FindControl<Button>("RedoButton");
        _readerProgressTrack = this.FindControl<Border>("ReaderProgressTrack");
        _readerProgressThumb = this.FindControl<Border>("ReaderProgressThumb");
        _chapterMarkersCanvas = this.FindControl<Canvas>("ChapterMarkersCanvas");

        // ── Annotation toolbar controls ──────────────────────────────────────
        _annotationSection  = this.FindControl<Border>("AnnotationSection");
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

        // Build theme swatch buttons.
        BuildThemeSwatches();

        if (DataContext is MyBibleApp.ViewModels.ScriptureViewModel vm)
        {
            _paragraphs = vm.Paragraphs;
            RebuildChapterGroups();
            ReinitializeWindow();
        }

        if (_paragraphList == null || _annotationToggle == null) return;

        if (_readerProgressTrack != null)
            _readerProgressTrack.SizeChanged += (_, _) => RefreshReaderProgress();

        // ── Scroll offset tracking ───────────────────────────────────────────
        EnsureScrollTrackingAttached();
        RefreshReaderProgress();

        // ── Pen event routing → InkOverlay ──────────────────────────────────
        // Attach to the full content grid (not just the ListBox) so strokes can
        // begin anywhere in the visible area, including the whitespace margins
        // that appear outside the text column when a journal layout sets MaxWidth.
        (_inkAreaGrid ?? (InputElement?)_paragraphList)?.AddHandler(
            PointerPressedEvent, OnListBoxPenPressed, handledEventsToo: true);
        
        // ── Mouse drag scrolling ─────────────────────────────────────────────
        _paragraphList.AddHandler(PointerPressedEvent, OnListBoxMousePressed,
            handledEventsToo: true);
        _paragraphList.AddHandler(PointerMovedEvent, OnListBoxMouseMoved,
            handledEventsToo: true);
        _paragraphList.AddHandler(PointerReleasedEvent, OnListBoxMouseReleased,
            handledEventsToo: true);

        // ── Margin touch panning (journal mode: finger drag outside text column) ──
        if (_inkAreaGrid != null)
        {
            _inkAreaGrid.AddHandler(PointerPressedEvent, OnMarginTouchPressed, handledEventsToo: false);
            _inkAreaGrid.AddHandler(PointerMovedEvent, OnMarginTouchMoved, handledEventsToo: false);
            _inkAreaGrid.AddHandler(PointerReleasedEvent, OnMarginTouchReleased, handledEventsToo: false);
            // Keep ink canvas column-offset in sync when the viewport is resized.
            _inkAreaGrid.SizeChanged += (_, _) => UpdateInkTextColumnOffset();
        }

        _annotationToggle.IsCheckedChanged += OnAnnotationToggleChanged;
        UpdateAnnotationState();

        // ── Scrollbar visibility (desktop: always visible; mobile: tap-to-reveal) ──
        if (!PlatformHelper.IsDesktop && _readerProgressTrack != null)
        {
            _readerProgressTrack.Opacity = 0;
            _paragraphList.AddHandler(TappedEvent, OnListBoxTapped, handledEventsToo: false);
        }
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
        RebuildParagraphTopCache();
    }

    private void OnParagraphScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_paragraphScrollViewer == null)
            return;

        // During tab switch, suppress all scroll-driven side effects until restore completes.
        if (_suppressScrollEventsForTabSwitch)
            return;

        _inkOverlay?.UpdateScrollOffset(_paragraphScrollViewer.Offset.Y);
        UpdateReaderProgress(_paragraphScrollViewer);

        // On mobile, briefly reveal the scrollbar during touch scrolling.
        if (!PlatformHelper.IsDesktop)
            ShowScrollbarBriefly();

        // Don't interfere while the user is dragging the scrollbar thumb.
        if (_isDraggingProgressBar) return;

        // Track scroll velocity to reveal chapter markers only during fast scrolling.
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastScrollTime).TotalSeconds;
        var currentOffset = _paragraphScrollViewer.Offset.Y;

        if (elapsed > 0 && elapsed < 1)
        {
            var velocity = Math.Abs(currentOffset - _lastScrollOffset) / elapsed;

            if (velocity >= ScrollVelocityThreshold)
            {
                _fastScrollCount++;
                if (!_chapterMarkersShownByScroll && _fastScrollCount >= FastScrollCountThreshold)
                {
                    _chapterMarkersShownByScroll = true;
                    BuildChapterMarkers();
                }
            }
            else
            {
                _fastScrollCount = 0;
            }
        }

        _lastScrollOffset = currentOffset;
        _lastScrollTime = now;

        // Reset the "scroll stopped" timer — hide markers after scrolling stops.
        _scrollStopCts?.Cancel();
        _scrollStopCts = new CancellationTokenSource();
        var cts = _scrollStopCts;
        _ = Task.Delay(800, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                _fastScrollCount = 0;
                if (_chapterMarkersShownByScroll)
                {
                    _chapterMarkersShownByScroll = false;
                    if (_chapterMarkersCanvas != null)
                        _chapterMarkersCanvas.IsVisible = false;
                }
            });
        }, TaskScheduler.Default);

        // Defer windowing decisions to after the current layout cycle.
        // Calling CheckWindowBounds synchronously here causes a feedback loop:
        //   layout → ScrollChanged → modify items → InvalidateMeasure → layout → ...
        // Running it at Loaded priority breaks the chain so Avalonia never sees
        // more than one layout pass per windowing adjustment.
        if (!_windowCheckPending && !_isAdjustingWindow)
        {
            _windowCheckPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _windowCheckPending = false;
                CheckWindowBounds();
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptureViewModel.Paragraphs) && sender is ScriptureViewModel vm)
        {
            _paragraphs = vm.Paragraphs;
            RebuildChapterGroups();
            ReinitializeWindow();
        }
        // Don't call RefreshReaderProgress() here — the ListBox is mid-render when
        // Paragraphs changes; reading the visual tree now gives stale positions.
        // OnParagraphScrollChanged fires naturally once the list settles.
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

    // ── Bible Reading overlay ─────────────────────────────────────────────────

    private void OnBibleReadingButtonClick(object? sender, RoutedEventArgs e) =>
        BibleReadingRequested?.Invoke(this, EventArgs.Empty);

    private void OnJournalsButtonClick(object? sender, RoutedEventArgs e) =>
        JournalsRequested?.Invoke(this, EventArgs.Empty);

    // ── Split-view toggle ────────────────────────────────────────────────────

    private void OnSplitViewToggleIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressSplitEvent) return;
        SplitToggled?.Invoke(this, _splitViewToggle?.IsChecked == true);
    }

    /// <summary>Captures the current ink strokes so they can be stored per-tab.</summary>
    public Controls.InkOverlayCanvas.InkState? CaptureInkState() => _inkOverlay?.CaptureState();

    /// <summary>Restores a previously captured ink state when switching back to a tab.</summary>
    public void RestoreInkState(Controls.InkOverlayCanvas.InkState? state) => _inkOverlay?.RestoreState(state);

    /// <summary>Captures the current scroll offset so it can be stored per-tab.</summary>
    public double? CaptureScrollOffset()
    {
        var offset = _paragraphScrollViewer?.Offset.Y;
        if (DataContext is ScriptureViewModel vm)
            vm.AppVM.AppendSyncDebugLog($"[Scroll] CaptureScrollOffset → Y={offset?.ToString("F1") ?? "null"}");
        return offset;
    }

    /// <summary>Forces the ink overlay scroll offset to match the current scroll viewer position.
    /// Call after loading journal strokes so the ink canvas renders at the right content-Y.</summary>
    public void SyncInkScrollOffset()
    {
        if (_inkOverlay == null || _paragraphScrollViewer == null) return;
        _inkOverlay.UpdateScrollOffset(_paragraphScrollViewer.Offset.Y);
        if (DataContext is ScriptureViewModel vm)
            vm.AppVM.AppendSyncDebugLog($"[Ink] SyncInkScrollOffset → Y={_paragraphScrollViewer.Offset.Y:F1}");
    }

    /// <summary>Suppresses scroll-driven side effects (header sync, chapter markers, reading progress).
    /// Call before changing DataContext during tab switches to prevent stale scroll events.</summary>
    public void SuppressScrollEvents()
    {
        _suppressScrollEventsForTabSwitch = true;
        _suppressReaderProgressSync = true;
    }

    /// <summary>Navigates to a verse and then clears scroll suppression flags.
    /// Used during tab switches when there is no saved scroll offset.</summary>
    public async Task NavigateToVerseAndUnsuppressAsync(int chapter, int verse)
    {
        try
        {
            await ScrollToReferenceAsync(chapter, verse);
        }
        finally
        {
            // Clear suppression after one more layout pass so the offset is stable.
            Dispatcher.UIThread.Post(() =>
            {
                _suppressScrollEventsForTabSwitch = false;
                _suppressReaderProgressSync = false;
                // Sync the ink overlay to the current scroll position.
                if (_paragraphScrollViewer != null)
                    _inkOverlay?.UpdateScrollOffset(_paragraphScrollViewer.Offset.Y);
                RefreshReaderProgress();
                if (DataContext is ScriptureViewModel vm)
                    vm.AppVM.AppendSyncDebugLog("[Scroll] Suppress flags cleared");
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnScrollRestoreLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingScrollRestoreY == null || _paragraphScrollViewer == null)
        {
            FinishScrollRestore();
            return;
        }

        var target = _pendingScrollRestoreY.Value;
        var current = _paragraphScrollViewer.Offset.Y;
        _scrollRestoreRetries++;

        // If the offset is already where we want it, count as stable.
        if (Math.Abs(current - target) < 1.0)
        {
            if (DataContext is ScriptureViewModel vm)
                vm.AppVM.AppendSyncDebugLog($"[Scroll] RestoreScrollOffset stable at Y={current:F1} (after {_scrollRestoreRetries} passes)");
            FinishScrollRestore();
            return;
        }

        // Safety: don't loop forever.
        if (_scrollRestoreRetries > 20)
        {
            if (DataContext is ScriptureViewModel vm)
                vm.AppVM.AppendSyncDebugLog($"[Scroll] RestoreScrollOffset gave up after {_scrollRestoreRetries} passes (current={current:F1}, target={target:F1})");
            FinishScrollRestore();
            return;
        }

        // Re-apply the offset on each layout pass until it sticks.
        _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, target);
    }

    private void FinishScrollRestore()
    {
        _pendingScrollRestoreY = null;
        ClearScrollRestoreHook();

        // Clear suppression after one more layout pass so the offset is stable.
        Dispatcher.UIThread.Post(() =>
        {
            _suppressScrollEventsForTabSwitch = false;
            _suppressReaderProgressSync = false;
            // Sync the ink overlay to the current scroll position (it was not updated
            // while suppression was active, which causes drift over multiple tab switches).
            if (_paragraphScrollViewer != null)
                _inkOverlay?.UpdateScrollOffset(_paragraphScrollViewer.Offset.Y);
            RefreshReaderProgress();
            if (DataContext is ScriptureViewModel vm)
                vm.AppVM.AppendSyncDebugLog("[Scroll] Suppress flags cleared");
        }, DispatcherPriority.Loaded);
    }

    private void ClearScrollRestoreHook()
    {
        if (_paragraphList != null)
            _paragraphList.LayoutUpdated -= OnScrollRestoreLayoutUpdated;
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

        // Show / hide the annotation section of the floating toolbar.
        if (_annotationSection != null)
            _annotationSection.IsVisible = isAnnotating;

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

    private void OnRedoButtonClick(object? sender, RoutedEventArgs e)
    {
        _inkOverlay?.RedoStroke();
    }

    // ── Toolbar: colour selection ─────────────────────────────────────────────

    private void OnColorSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Tag is not string colorHex) return;
        var color = Color.Parse(colorHex);

        ApplyColor(color);
        SetActiveColorSwatch(button);

        // Reset the custom-colour button back to its default appearance.
        if (_customColorButton != null)
            _customColorButton.ClearValue(Button.BackgroundProperty);

        // Sync ColorView so it reflects the chosen preset.
        if (_colorPickerView != null) _colorPickerView.Color = color;

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

        // Position the custom thumb using paragraph-index fraction (works with
        // virtualization), but detect true top/bottom via ScrollViewer offset
        // so the thumb reaches both extremes.
        if (_readerProgressTrack != null && _readerProgressThumb != null && !_isDraggingProgressBar
            && !_suppressScrollEventsForTabSwitch)
        {
            double fraction;
            var scrollableHeight = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
            if (scrollViewer.Offset.Y <= 0)
            {
                fraction = 0;
            }
            else if (scrollableHeight > 0 && scrollViewer.Offset.Y >= scrollableHeight - 1)
            {
                fraction = 1;
            }
            else
            {
                var (topPara, topOff) = GetTopVisibleParagraph();
                if (topPara != null)
                {
                    var idx = FindParagraphIndex(topPara);
                    var maxIndex = Math.Max(1, _paragraphs.Count - 1);
                    fraction = idx >= 0 ? Math.Clamp((idx + topOff) / maxIndex, 0, 1) : 0;
                }
                else
                {
                    fraction = 0;
                }
            }

            var trackHeight = _readerProgressTrack.Bounds.Height;
            var thumbHeight = _readerProgressThumb.Height;
            var maxTop      = Math.Max(0, trackHeight - thumbHeight);
            Canvas.SetTop(_readerProgressThumb, fraction * maxTop);
        }

        if (_suppressReaderProgressSync) return;

        var (topParagraph, _) = GetTopVisibleParagraph();
        if (topParagraph == null) return;

        if (DataContext is ScriptureViewModel vm)
        {
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

        if (fraction <= 0 && _paragraphScrollViewer != null)
        {
            EnsureChapterInWindow(1);
            _paragraphScrollViewer.Offset = new Avalonia.Vector(0, 0);
            return;
        }

        if (fraction >= 1 && _paragraphScrollViewer != null)
        {
            var lastChapter = _chapterGroups.Count;
            EnsureChapterInWindow(lastChapter);
            var scrollableHeight = _paragraphScrollViewer.Extent.Height - _paragraphScrollViewer.Viewport.Height;
            if (scrollableHeight > 0)
                _paragraphScrollViewer.Offset = new Avalonia.Vector(0, scrollableHeight);
            return;
        }

        var targetIndex = (int)(fraction * (_paragraphs.Count - 1));
        var targetPara  = _paragraphs[targetIndex];
        EnsureChapterInWindow(targetPara.StartChapter);
        _paragraphList.ScrollIntoView(targetPara);
    }

    private void OnProgressTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_readerProgressTrack == null) return;
        _isDraggingProgressBar = true;
        e.Pointer.Capture(_readerProgressTrack);
        // Keep the scrollbar visible while the thumb is being dragged.
        if (!PlatformHelper.IsDesktop)
            _scrollbarHideCts?.Cancel();
        BuildChapterMarkers();
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
        if (_chapterMarkersCanvas != null)
            _chapterMarkersCanvas.IsVisible = false;
        // Restart the auto-hide countdown after the thumb is released.
        if (!PlatformHelper.IsDesktop)
            ShowScrollbarBriefly();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Mobile scrollbar auto-hide ────────────────────────────────────────────

    private void OnListBoxTapped(object? sender, TappedEventArgs e) =>
        ShowScrollbarBriefly();

    private void ShowScrollbarBriefly()
    {
        if (_readerProgressTrack == null) return;
        _readerProgressTrack.Opacity = 1;

        _scrollbarHideCts?.Cancel();
        _scrollbarHideCts = new CancellationTokenSource();
        var cts = _scrollbarHideCts;

        _ = Task.Delay(2000, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isDraggingProgressBar && _readerProgressTrack != null)
                    _readerProgressTrack.Opacity = 0;
            });
        }, TaskScheduler.Default);
    }

    private void BuildChapterMarkers()
    {
        if (_chapterMarkersCanvas == null || _readerProgressTrack == null || _paragraphs.Count == 0)
            return;

        _chapterMarkersCanvas.Children.Clear();

        var trackHeight = _readerProgressTrack.Bounds.Height;
        var total       = (double)_paragraphs.Count;
        const double LabelHalfHeight = 9.0; // half of approx label height for vertical centering

        for (var i = 0; i < _paragraphs.Count; i++)
        {
            var p = _paragraphs[i];
            if (!p.HasChapterDropCap) continue;

            var fraction = i / total;
            var top      = Math.Clamp(fraction * trackHeight - LabelHalfHeight, 0, trackHeight - LabelHalfHeight * 2);

            var label = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background   = new SolidColorBrush(Color.FromArgb(0xCC, 0xA0, 0xA0, 0xA0)),
                Padding      = new Thickness(5, 2),
                Child        = new TextBlock
                {
                    Text       = p.ChapterDropCap!.Value.ToString(),
                    FontSize   = 11,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold,
                }
            };

            Canvas.SetTop(label, top);
            Canvas.SetRight(label, 0);
            _chapterMarkersCanvas.Children.Add(label);
        }

        _chapterMarkersCanvas.IsVisible = true;
    }

    private async void OnLookupGoButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingLookupSelection) return;

        if (DataContext is not ScriptureViewModel vm) return;
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
            {
                _suppressReaderProgressSync = true;
                try { await ScrollToReferenceAsync(requestedChapter, requestedVerse); }
                finally { _suppressReaderProgressSync = false; }
            }

            if (_headerLookupButton?.Flyout is Flyout flyout)
                flyout.Hide();
        }
        finally
        {
            _isApplyingLookupSelection = false;
        }
    }


    /// <summary>Navigates the scroll position to the given chapter and verse.</summary>
    public Task NavigateToVerseAsync(int chapter, int verse) => ScrollToReferenceAsync(chapter, verse);

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

        EnsureChapterInWindow(target.StartChapter);

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

    /// <summary>
    /// Rebuilds _chapterGroups and _paragraphChapterInfo from _paragraphs.
    /// O(N) in paragraph count. Call whenever _paragraphs changes.
    /// </summary>
    private void RebuildChapterGroups()
    {
        (_chapterGroups, _paragraphChapterInfo) = ChapterGroupBuilder.Build(_paragraphs);
        _chapterStartY.Clear();
        _chapterLocalTops.Clear();
    }

    /// <summary>
    /// Resets the scroll window to the first N chapters that fill 3× viewport height.
    /// Call whenever _paragraphs / _chapterGroups changes.
    /// </summary>
    private void ReinitializeWindow()
    {
        _isAdjustingWindow = true;
        try
        {
            _windowedItems.Clear();
            _windowStart = 0;
            _windowEnd   = 0;
            _chapterStartY.Clear();
            _chapterLocalTops.Clear();

            if (_chapterGroups.Count == 0) return;

            // Wait for viewport to be known; if not yet, use a fallback.
            var vpHeight = _paragraphScrollViewer?.Viewport.Height;
            var targetHeight = (vpHeight > 0 ? vpHeight.Value : 800) * 3;
            ExtendWindowDown(targetHeight);
        }
        finally
        {
            _isAdjustingWindow = false;
        }
    }

    /// <summary>
    /// Adds chapters at the bottom of the window until the added content height
    /// reaches targetHeight or the end of the book is reached.
    /// Fires ChapterEnteredWindow for each chapter added.
    /// </summary>
    private void ExtendWindowDown(double targetHeight = 0)
    {
        double added = 0;
        while (_windowEnd < _chapterGroups.Count && (targetHeight <= 0 || added < targetHeight))
        {
            var chapter = _windowEnd + 1;   // 1-based chapter number
            foreach (var para in _chapterGroups[_windowEnd])
                _windowedItems.Add(para);

            _windowEnd++;
            added += EstimateChapterHeight(chapter);
            ChapterEnteredWindow?.Invoke(this, chapter);
        }
    }

    /// <summary>
    /// Adds chapters at the top of the window. Compensates scroll offset for inserted height.
    /// Fires ChapterEnteredWindow for each chapter added.
    /// </summary>
    private void ExtendWindowUp()
    {
        if (_windowStart == 0 || _paragraphScrollViewer == null) return;

        _windowStart--;
        var chapter = _windowStart + 1;     // 1-based
        var newParagraphs = _chapterGroups[_windowStart];
        var estimatedHeight = EstimateChapterHeight(chapter);

        // Prepend paragraphs (ObservableCollection has no AddRange; insert individually).
        for (var i = newParagraphs.Count - 1; i >= 0; i--)
            _windowedItems.Insert(0, newParagraphs[i]);

        // Compensate scroll offset so visible content doesn't jump.
        var newOffset = _paragraphScrollViewer.Offset.Y + estimatedHeight;
        _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, newOffset);

        ChapterEnteredWindow?.Invoke(this, chapter);
    }

    /// <summary>
    /// Removes the topmost chapter from the window.
    /// Compensates scroll offset for the removed height.
    /// Fires ChapterExitedWindow.
    /// </summary>
    private void TrimWindowTop()
    {
        if (_windowEnd - _windowStart <= 1 || _paragraphScrollViewer == null) return;

        var chapter = _windowStart + 1;     // 1-based
        var removedParagraphs = _chapterGroups[_windowStart];
        var removedHeight = MeasureChapterHeight(chapter) ?? EstimateChapterHeight(chapter);

        // Remove paragraphs from the start of _windowedItems.
        for (var i = 0; i < removedParagraphs.Count; i++)
            _windowedItems.RemoveAt(0);

        _windowStart++;

        // Compensate scroll offset downward.
        var newOffset = Math.Max(0, _paragraphScrollViewer.Offset.Y - removedHeight);
        _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, newOffset);

        ChapterExitedWindow?.Invoke(this, chapter);
    }

    /// <summary>
    /// Removes the bottommost chapter from the window.
    /// No scroll offset compensation needed (removing from end doesn't shift content).
    /// Fires ChapterExitedWindow.
    /// </summary>
    private void TrimWindowBottom()
    {
        if (_windowEnd - _windowStart <= 1) return;

        _windowEnd--;
        var chapter = _windowEnd + 1;       // 1-based
        var removedParagraphs = _chapterGroups[_windowEnd];

        for (var i = 0; i < removedParagraphs.Count; i++)
            _windowedItems.RemoveAt(_windowedItems.Count - 1);

        ChapterExitedWindow?.Invoke(this, chapter);
    }

    /// <summary>
    /// Estimates chapter height from verse count × average line height.
    /// Used for scroll offset compensation when the chapter is not yet realized.
    /// ~60px per paragraph is a conservative estimate.
    /// </summary>
    private double EstimateChapterHeight(int chapter)
    {
        // chapter is 1-based; _chapterGroups is 0-based.
        var groupIdx = chapter - 1;
        if (groupIdx < 0 || groupIdx >= _chapterGroups.Count) return 600;
        var paragraphCount = _chapterGroups[groupIdx].Count;
        return paragraphCount * 60;    // 60 px per paragraph
    }

    /// <summary>
    /// Returns the actual measured height of a chapter by summing realized ListBoxItem heights.
    /// Returns null if the chapter is not currently realized.
    /// </summary>
    private double? MeasureChapterHeight(int chapter)
    {
        if (_paragraphList == null) return null;

        double total = 0;
        bool found = false;

        foreach (var item in _paragraphList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.DataContext is not BibleParagraph para) continue;
            if (!_paragraphChapterInfo.TryGetValue(para, out var info)) continue;
            if (info.Chapter != chapter) continue;
            total += item.Bounds.Height;
            found = true;
        }

        return found ? total : null;
    }

    private void CheckWindowBounds()
    {
        if (_isAdjustingWindow) return;
        if (_paragraphScrollViewer == null || _chapterGroups.Count == 0) return;

        // Don't make trimming/extending decisions when the viewport has not been
        // measured yet — vpHeight == 0 makes every condition evaluate incorrectly
        // (e.g. "contentBottom > 0" looks like "trim" when we should do nothing).
        var vpHeight = _paragraphScrollViewer.Viewport.Height;
        if (vpHeight <= 0) return;

        _isAdjustingWindow = true;
        try
        {
            var scrollTop     = _paragraphScrollViewer.Offset.Y;
            var scrollBottom  = scrollTop + vpHeight;
            var contentBottom = _paragraphScrollViewer.Extent.Height;

            // Extend down when within 1 viewport of the window bottom.
            // Use a 3× target to overshoot because EstimateChapterHeight is
            // usually 2–3× larger than actual rendered height on desktop, so
            // each call adds roughly 1 real viewport of content.
            if (_windowEnd < _chapterGroups.Count && contentBottom - scrollBottom < vpHeight)
                ExtendWindowDown(vpHeight * 3);

            // Extend up when within half a viewport of the window top.
            if (_windowStart > 0 && scrollTop < vpHeight * 0.5)
                ExtendWindowUp();

            // Trim top only when 5+ viewports above the current scroll position.
            // Wide hysteresis (extend < 1×, trim > 5×) prevents extend↔trim
            // oscillation when individual chapter heights exceed 1 viewport.
            if (_windowEnd - _windowStart > 1 && scrollTop > vpHeight * 5)
                TrimWindowTop();

            // Trim bottom only when 5+ viewports below the current scroll position.
            if (_windowEnd - _windowStart > 1 && contentBottom - scrollBottom > vpHeight * 5)
                TrimWindowBottom();
        }
        finally
        {
            _isAdjustingWindow = false;
        }
    }

    /// <summary>
    /// Synchronously repositions the window so the given chapter is realized.
    /// Called before ScrollIntoView jumps to a target outside the current window.
    /// </summary>
    private void EnsureChapterInWindow(int chapter)
    {
        var groupIdx = chapter - 1;
        if (groupIdx < 0 || groupIdx >= _chapterGroups.Count) return;

        // Check if already in window.
        if (groupIdx >= _windowStart && groupIdx < _windowEnd) return;

        _isAdjustingWindow = true;
        try
        {
            // Rebuild window centered on target chapter.
            _windowedItems.Clear();
            _chapterStartY.Clear();
            _chapterLocalTops.Clear();

            // Load target chapter + 2 either side (for buffer).
            _windowStart = Math.Max(0, groupIdx - 2);
            _windowEnd   = _windowStart;

            var targetEnd = Math.Min(_chapterGroups.Count, groupIdx + 3);

            while (_windowEnd < targetEnd)
            {
                var ch = _windowEnd + 1;
                foreach (var para in _chapterGroups[_windowEnd])
                    _windowedItems.Add(para);
                _windowEnd++;
                ChapterEnteredWindow?.Invoke(this, ch);
            }

            // Adjust scroll offset to top (will be corrected by layout once items are realized).
            if (_paragraphScrollViewer != null)
                _paragraphScrollViewer.Offset = new Vector(0, 0);
        }
        finally
        {
            _isAdjustingWindow = false;
        }
    }

    /// <summary>Adds stokes for chapters entering the window (with legacy anchor migration).</summary>
    public void AppendChapterStrokes(IReadOnlyList<JournalInkStroke> strokes)
    {
        var migrated = InkAnchorMigrator.Migrate(strokes, _paragraphChapterInfo, _paragraphs);
        _inkOverlay?.AppendChapterStrokes(migrated);
    }

    /// <summary>Removes strokes for a chapter leaving the window.</summary>
    public void RemoveChapterStrokes(int chapter) =>
        _inkOverlay?.RemoveChapterStrokes(chapter);

    private IReadOnlyList<JournalInkStroke> MigrateStrokeAnchors(IReadOnlyList<JournalInkStroke> strokes) =>
        InkAnchorMigrator.Migrate(strokes, _paragraphChapterInfo, _paragraphs);

    // ── Ink paragraph anchoring helpers ───────────────────────────────────────

    /// <summary>
    /// Walks the visual tree once and caches every paragraph's content-space Y.
    /// Called after layout settles; safe to call repeatedly (cheap if no change).
    /// </summary>
    private void RebuildParagraphTopCache()
    {
        if (_paragraphList == null || _paragraphScrollViewer == null)
            return;

        var byChapter = new Dictionary<int, List<(int LocalIndex, double ViewportY)>>();

        foreach (var item in _paragraphList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.DataContext is not BibleParagraph para) continue;
            if (!_paragraphChapterInfo.TryGetValue(para, out var info)) continue;

            var viewportY = item.TranslatePoint(default, _paragraphScrollViewer)?.Y;
            if (viewportY == null) continue;

            if (!byChapter.TryGetValue(info.Chapter, out var list))
            {
                list = [];
                byChapter[info.Chapter] = list;
            }
            list.Add((info.LocalIndex, viewportY.Value));
        }

        var scrollY = _paragraphScrollViewer.Offset.Y;

        foreach (var (chapter, items) in byChapter)
        {
            items.Sort((a, b) => a.LocalIndex.CompareTo(b.LocalIndex));
            var chapterViewportTop = items.Count > 0 ? items[0].ViewportY : 0;
            _chapterStartY[chapter] = scrollY + chapterViewportTop;

            var maxLocalIndex = items.Count > 0 ? items[^1].LocalIndex : 0;
            var localTops = new double[maxLocalIndex + 1];
            Array.Fill(localTops, -1.0);
            foreach (var (localIndex, viewportY) in items)
                localTops[localIndex] = viewportY - chapterViewportTop;

            _chapterLocalTops[chapter] = localTops;
        }
    }

    /// <summary>O(1) paragraph content-top lookup for the ink drift-correction callback.</summary>
    private double? GetParagraphContentTopFast(int chapter, int withinChapterIndex)
    {
        if (!_chapterStartY.TryGetValue(chapter, out var chapterY)) return null;
        if (!_chapterLocalTops.TryGetValue(chapter, out var localTops)) return null;
        if (withinChapterIndex < 0 || withinChapterIndex >= localTops.Length) return null;
        var local = localTops[withinChapterIndex];
        return local >= 0 ? chapterY + local : null;
    }

    private (int Chapter, int LocalIndex, double ContentTop)? FindParagraphAtContentY(double contentY)
    {
        if (_paragraphList == null || _paragraphScrollViewer == null || _paragraphs.Count == 0)
            return null;

        var scrollY = _paragraphScrollViewer.Offset.Y;
        (int Chapter, int LocalIndex, double ContentTop, double Height)? best = null;
        double bestDist = double.MaxValue;

        foreach (var item in _paragraphList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.DataContext is not BibleParagraph para) continue;
            if (!_paragraphChapterInfo.TryGetValue(para, out var info)) continue;

            var top = item.TranslatePoint(default, _paragraphScrollViewer)?.Y;
            if (top == null) continue;

            var contentTop = scrollY + top.Value;
            var height = item.Bounds.Height;

            if (contentY >= contentTop && contentY <= contentTop + height)
                return (info.Chapter, info.LocalIndex, contentTop);

            var dist = Math.Min(Math.Abs(contentY - contentTop),
                                Math.Abs(contentY - (contentTop + height)));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = (info.Chapter, info.LocalIndex, contentTop, height);
            }
        }

        return best.HasValue ? (best.Value.Chapter, best.Value.LocalIndex, best.Value.ContentTop) : null;
    }

    /// <summary>
    /// Given a paragraph index, returns its current content-space top if
    /// the paragraph is currently realized in the visual tree.
    /// </summary>
    private double? GetParagraphContentTopByIndex(int paragraphIndex)
    {
        if (_paragraphList == null || _paragraphScrollViewer == null ||
            paragraphIndex < 0 || paragraphIndex >= _paragraphs.Count)
            return null;

        var targetPara = _paragraphs[paragraphIndex];

        foreach (var item in _paragraphList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (!ReferenceEquals(item.DataContext, targetPara)) continue;
            var top = item.TranslatePoint(default, _paragraphScrollViewer)?.Y;
            if (top == null) continue;
            return _paragraphScrollViewer.Offset.Y + top.Value;
        }

        return null;
    }


    // ── Settings flyout handlers ─────────────────────────────────────────────

    // ── Chapter navigation toolbar ────────────────────────────────────────────

    private async void OnPrevChapterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptureViewModel vm) return;
        vm.GoToPreviousChapter();
        vm.Header = $"{vm.BookTitle} {vm.SelectedLookupChapter}:{vm.SelectedLookupVerse}";
        _suppressReaderProgressSync = true;
        try { await ScrollToReferenceAsync(vm.SelectedLookupChapter, 1); }
        finally { _suppressReaderProgressSync = false; }
    }

    private async void OnNextChapterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptureViewModel vm) return;
        vm.GoToNextChapter();
        vm.Header = $"{vm.BookTitle} {vm.SelectedLookupChapter}:{vm.SelectedLookupVerse}";
        _suppressReaderProgressSync = true;
        try { await ScrollToReferenceAsync(vm.SelectedLookupChapter, 1); }
        finally { _suppressReaderProgressSync = false; }
    }

    // ── Theme swatches ──────────────────────────────────────────────────────

    private void BuildThemeSwatches()
    {
        if (_themeSwatchPanel == null) return;
        _themeSwatchPanel.Children.Clear();

        var currentId = (DataContext as ScriptureViewModel)?.AppVM.SelectedThemeId
                        ?? Models.AppTheme.LightWhite.Id;

        foreach (var theme in Models.AppTheme.All)
        {
            var btn = new Button
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(0),
                Tag = theme.Id,
                Background = new SolidColorBrush(theme.SwatchColor),
                BorderThickness = new Thickness(theme.SwatchColor == Colors.White ? 1 : 2),
                BorderBrush = theme.Id == currentId
                    ? new SolidColorBrush(Color.Parse("#0078D7"))
                    : new SolidColorBrush(Color.Parse(
                        theme.SwatchColor == Colors.White ? "#CCCCCC" : "#00000000")),
            };
            Avalonia.Controls.ToolTip.SetTip(btn, theme.Label);

            btn.Click += OnThemeSwatchClick;
            _themeSwatchPanel.Children.Add(btn);
        }
    }

    private void OnThemeSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string themeId) return;
        if (DataContext is not ScriptureViewModel vm) return;

        vm.AppVM.SelectedThemeId = themeId;
        var theme = Models.AppTheme.GetById(themeId);
        ApplyTheme(theme);

        // Update swatch borders to highlight the active one.
        if (_themeSwatchPanel == null) return;
        foreach (var child in _themeSwatchPanel.Children)
        {
            if (child is not Button b) continue;
            var isActive = (b.Tag as string) == themeId;
            var t = Models.AppTheme.GetById(b.Tag as string);
            b.BorderBrush = new SolidColorBrush(isActive
                ? Color.Parse("#0078D7")
                : Color.Parse(t.SwatchColor == Colors.White ? "#CCCCCC" : "#00000000"));
        }
    }

    /// <summary>Apply the given theme: set variant + resource overrides.</summary>
    public void ApplyTheme(Models.AppTheme theme)
    {
        if (Application.Current == null) return;
        Application.Current.RequestedThemeVariant = theme.Variant;

        // Apply or clear background/foreground overrides on the application resources.
        if (theme.BackgroundOverride is { } bg)
            Application.Current.Resources["ThemeBackgroundBrush"] = new SolidColorBrush(bg);
        else
            Application.Current.Resources.Remove("ThemeBackgroundBrush");

        if (theme.ForegroundOverride is { } fg)
            Application.Current.Resources["ThemeForegroundBrush"] = new SolidColorBrush(fg);
        else
            Application.Current.Resources.Remove("ThemeForegroundBrush");
    }

    private async void OnSyncAuthButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScriptureViewModel vm)
            await vm.AppVM.AuthenticateAsync();
    }

    private void OnSyncSignOutButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScriptureViewModel vm)
            vm.AppVM.SignOut();
    }

    private void OnSyncForceSyncButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScriptureViewModel vm)
            vm.AppVM.ForceSync();
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

    // ── Margin touch panning ─────────────────────────────────────────────────

    private void OnMarginTouchPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return;
        if (_inkAreaGrid == null || _paragraphList == null) return;

        // Only activate when the touch lands outside the text column.
        var pos = e.GetPosition(_inkAreaGrid);
        if (_paragraphList.Bounds.Contains(pos)) return;

        _isTouchPanning = true;
        _lastTouchPosition = e.GetPosition(_inkAreaGrid);
        e.Pointer.Capture(_inkAreaGrid);
        e.Handled = true;
    }

    private void OnMarginTouchMoved(object? sender, PointerEventArgs e)
    {
        if (!_isTouchPanning || _paragraphScrollViewer == null) return;
        if (e.Pointer.Type != PointerType.Touch) return;

        var currentPos = e.GetPosition(_inkAreaGrid);
        var deltaY = _lastTouchPosition.Y - currentPos.Y;

        var maxY = Math.Max(0, _paragraphScrollViewer.Extent.Height - _paragraphScrollViewer.Viewport.Height);
        var newOffset = Math.Clamp(_paragraphScrollViewer.Offset.Y + deltaY, 0, maxY);
        _paragraphScrollViewer.Offset = new Vector(_paragraphScrollViewer.Offset.X, newOffset);

        _lastTouchPosition = currentPos;
        e.Handled = true;
    }

    private void OnMarginTouchReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isTouchPanning || e.Pointer.Type != PointerType.Touch) return;

        _isTouchPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Journal integration ───────────────────────────────────────────────────

    public void SetActiveJournalName(string? name)
    {
        if (_activeJournalLabel == null) return;
        _activeJournalLabel.Text = name;
        _activeJournalLabel.IsVisible = name != null;
    }

    public void SetJournalFlyoutOpen(bool open)
    {
        if (_journalsHeaderButton == null) return;
        if (open)
            _journalsHeaderButton.Classes.Add("flyout-open");
        else
            _journalsHeaderButton.Classes.Remove("flyout-open");
    }

    public void SetUnsavedBadgeVisible(bool visible)
    {
        if (_journalUnsavedBadge != null)
            _journalUnsavedBadge.IsVisible = visible;
    }

    public void LoadJournalStrokes(IReadOnlyList<JournalInkStroke> strokes)
    {
        var migrated = MigrateStrokeAnchors(strokes);
        _inkOverlay?.LoadJournalStrokes(migrated);
    }

    public void SetJournalLayout(JournalLayout? layout)
    {
        if (_paragraphList == null) return;

        if (layout == null)
        {
            // Restore defaults
            _paragraphList.MaxWidth = double.PositiveInfinity;
            _paragraphList.FontSize = 19;
            _paragraphList.ClearValue(FontFamilyProperty);
        }
        else
        {
            if (layout.TextColumnWidthDip > 0)
                _paragraphList.MaxWidth = layout.TextColumnWidthDip;

            if (layout.FontSizeDip > 0)
                _paragraphList.FontSize = layout.FontSizeDip;

            if (!string.IsNullOrEmpty(layout.FontFamily))
                _paragraphList.FontFamily = new Avalonia.Media.FontFamily(layout.FontFamily);
        }

        // Update ink canvas text-column offset after layout settles.
        Dispatcher.UIThread.Post(UpdateInkTextColumnOffset, DispatcherPriority.Loaded);
    }

    private void UpdateInkTextColumnOffset()
    {
        if (_inkOverlay == null || _paragraphList == null) return;
        _inkOverlay.UpdateTextColumnOffsetX(_paragraphList.Bounds.X);
    }
}


