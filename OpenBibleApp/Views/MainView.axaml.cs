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
using Avalonia.VisualTree;
using OpenBibleApp.Controls;
using OpenBibleApp.Models;

namespace OpenBibleApp.Views;

public partial class MainView : UserControl
{
    private readonly Flyout _footnoteFlyout;
    private readonly SelectableTextBlock _footnoteTextBlock;
    private ListBox? _paragraphList;
    private ToggleSwitch? _annotationToggle;
    private InkOverlayCanvas? _inkOverlay;
    private string _lastPointerInfo = "–";
    private int _captureLostCount;
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
        _inkOverlay     = this.FindControl<InkOverlayCanvas>("InkOverlay");

        if (_paragraphList == null || _annotationToggle == null) return;

        // ── Scroll offset tracking ───────────────────────────────────────────
        // Keep the InkOverlay in sync with the ListBox scroll so strokes
        // appear anchored to the text content.
        var sv = _paragraphList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv != null && _inkOverlay != null)
        {
            sv.ScrollChanged += (_, _) => _inkOverlay.UpdateScrollOffset(sv.Offset.Y);
        }

        // ── Pen event routing → InkOverlay ──────────────────────────────────
        // When annotation is on and a PEN presses, capture the pointer to the
        // InkOverlay so all subsequent Moved/Released events go directly to it.
        // We listen in the bubble phase with handledEventsToo so we always fire.
        _paragraphList.AddHandler(PointerPressedEvent, OnListBoxPenPressed,
            handledEventsToo: true);

        // ── Debug logging ────────────────────────────────────────────────────
        _paragraphList.AddHandler(PointerMovedEvent, (_, ev) =>
        {
            if (ev.Pointer.Type == PointerType.Pen)
            {
                _lastPointerInfo = $"Move pen handled={ev.Handled}";
                UpdateDebugDisplay();
            }
        }, handledEventsToo: true);
        _paragraphList.AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            _captureLostCount++;
            _lastPointerInfo = $"CaptureLost #{_captureLostCount}";
            UpdateDebugDisplay();
        }, handledEventsToo: true);

        _annotationToggle.IsCheckedChanged += OnAnnotationToggleChanged;
        UpdateAnnotationState();
        UpdateDebugDisplay();
    }

    // ── Pen press routing ────────────────────────────────────────────────────

    private void OnListBoxPenPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_annotationToggle?.IsChecked != true) return;
        if (e.Pointer.Type != PointerType.Pen) return;
        if (_inkOverlay == null) return;

        _lastPointerInfo = $"PenPress → overlay";

        // Start a stroke at the pen's viewport position relative to the overlay.
        var pos = e.GetPosition(_inkOverlay);
        _inkOverlay.StartStroke(pos);

        // Capture the pointer to the overlay.
        // All subsequent PointerMoved / PointerReleased events now go directly
        // to InkOverlay.OnPointerMoved / OnPointerReleased, bypassing the ListBox.
        e.Pointer.Capture(_inkOverlay);
        e.Handled = true;

        UpdateDebugDisplay();
    }

    // ── Annotation toggle / scroll gesture swap ───────────────────────────────

    private void OnAnnotationToggleChanged(object? sender, RoutedEventArgs e)
    {
        UpdateAnnotationState();
        UpdateDebugDisplay();
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

    // ── Debug overlay ─────────────────────────────────────────────────────────

    private void UpdateDebugDisplay()
    {
        bool isAnnotating = _annotationToggle?.IsChecked == true;
        var scp = _paragraphList?.GetVisualDescendants()
            .OfType<ScrollContentPresenter>().FirstOrDefault();
        int touchOnlyCount = scp?.GestureRecognizers
            .OfType<TouchOnlyScrollGestureRecognizer>().Count() ?? 0;
        int strokes = _inkOverlay != null ? 0 : 0; // shown via debug text below

        this.FindControl<TextBlock>("DebugAnnotatingStatus")!.Text =
            $"Ann:{(isAnnotating ? "ON" : "OFF")}";
        this.FindControl<TextBlock>("DebugPointerStatus")!.Text =
            $"Last: {_lastPointerInfo}";
        this.FindControl<TextBlock>("DebugStrokeStatus")!.Text =
            $"CapLost:{_captureLostCount}";
        this.FindControl<TextBlock>("DebugScrollStatus")!.Text =
            $"SCP:{scp != null}  TouchOnly:{touchOnlyCount}  Saved:{_savedScrollRecognizers.Count}";
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