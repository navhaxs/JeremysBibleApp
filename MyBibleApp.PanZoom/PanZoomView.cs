using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyBibleApp.PanZoom;

/// <summary>
/// Reports <c>child natural size × Scale</c> as its own desired size (so the ScrollViewer's
/// extent/scrollbars track the zoomed size correctly), while always measuring its child
/// unconstrained. That decoupling matters: an earlier version of this control used a plain
/// Border with an explicit Width/Height set to the scaled size, which — because that Border's
/// own (already-scaled, potentially tiny) size caps the measure constraint it hands its own
/// child — created a circular dependency between "how big the content naturally wants to be"
/// and "how big we last told our own parent we are". Once zoomed out far enough that collapsed
/// to a degenerate zero-size measurement, and stayed zero forever after: every later zoom
/// attempt (including zooming back in) read the same corrupted zero natural size and aborted,
/// so this panel's Width/Height was never touched again to recover. Measuring the child at
/// <see cref="Size.Infinity"/> here — independent of whatever Scale is currently set to — makes
/// that deadlock structurally impossible: the child's natural size is never influenced by this
/// panel's own last-reported size.
/// </summary>
internal sealed class ZoomHostPanel : Decorator
{
    public double Scale { get; set; } = 1.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(Size.Infinity);
        var size = Child?.DesiredSize ?? default;
        return new Size(size.Width * Scale, size.Height * Scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child != null)
            Child.Arrange(new Rect(Child.DesiredSize));
        return finalSize;
    }
}

/// <summary>
/// A <see cref="ContentControl"/> that hosts its content in a scrollable, pinch-and-wheel
/// zoomable viewport — pan with a single finger or the scrollbars, zoom by pinching or
/// Ctrl+scrolling, both anchored to the touch/cursor position rather than the viewport center.
///
/// Content is measured and arranged at its natural (unscaled) size; zoom is applied via a
/// <see cref="ScaleTransform"/> on the content, with a sizing host between it and the
/// <see cref="ScrollViewer"/> that reports the scaled size so the ScrollViewer's own
/// scrollbars/extent stay correct. Touch pinch is tracked manually (not via Avalonia's
/// built-in PinchGestureRecognizer) to avoid a known upstream issue where an interrupted
/// two-finger gesture leaves the recognizer's internal state stuck, permanently misreading
/// every later single-finger touch as a phantom pinch (see
/// github.com/AvaloniaUI/Avalonia/discussions/12351).
/// </summary>
public class PanZoomView : ContentControl
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PanZoomView, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<double> MinZoomProperty =
        AvaloniaProperty.Register<PanZoomView, double>(nameof(MinZoom), 0.25);

    public static readonly StyledProperty<double> MaxZoomProperty =
        AvaloniaProperty.Register<PanZoomView, double>(nameof(MaxZoom), 4.0);

    public static readonly StyledProperty<double> WheelZoomStepProperty =
        AvaloniaProperty.Register<PanZoomView, double>(nameof(WheelZoomStep), 0.1);

    public static readonly StyledProperty<ScrollBarVisibility> HorizontalScrollBarVisibilityProperty =
        AvaloniaProperty.Register<PanZoomView, ScrollBarVisibility>(nameof(HorizontalScrollBarVisibility),
            ScrollBarVisibility.Auto);

    public static readonly StyledProperty<ScrollBarVisibility> VerticalScrollBarVisibilityProperty =
        AvaloniaProperty.Register<PanZoomView, ScrollBarVisibility>(nameof(VerticalScrollBarVisibility),
            ScrollBarVisibility.Auto);

    /// <summary>Current zoom level. Setting it programmatically zooms toward the viewport center.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double MinZoom
    {
        get => GetValue(MinZoomProperty);
        set => SetValue(MinZoomProperty, value);
    }

    public double MaxZoom
    {
        get => GetValue(MaxZoomProperty);
        set => SetValue(MaxZoomProperty, value);
    }

    /// <summary>Zoom change per Ctrl+wheel notch.</summary>
    public double WheelZoomStep
    {
        get => GetValue(WheelZoomStepProperty);
        set => SetValue(WheelZoomStepProperty, value);
    }

    public ScrollBarVisibility HorizontalScrollBarVisibility
    {
        get => GetValue(HorizontalScrollBarVisibilityProperty);
        set => SetValue(HorizontalScrollBarVisibilityProperty, value);
    }

    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => GetValue(VerticalScrollBarVisibilityProperty);
        set => SetValue(VerticalScrollBarVisibilityProperty, value);
    }

    /// <summary>The inner ScrollViewer, for callers that need to scroll to a specific descendant (e.g. BringIntoView-style navigation).</summary>
    public ScrollViewer? InnerScrollViewer => _scrollViewer;

    private static readonly FuncControlTemplate DefaultTemplate = new((templatedControl, scope) =>
    {
        var control = (PanZoomView)templatedControl;
        var contentPresenter = new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
        };
        contentPresenter.RegisterInNameScope(scope);
        contentPresenter[!ContentPresenter.ContentProperty] = control[!ContentProperty];
        contentPresenter[!ContentPresenter.ContentTemplateProperty] = control[!ContentTemplateProperty];

        var zoomHost = new ZoomHostPanel
        {
            Name = "PART_ZoomHost",
            ClipToBounds = false,
            Child = contentPresenter,
        };
        zoomHost.RegisterInNameScope(scope);

        var scrollViewer = new ScrollViewer
        {
            Name = "PART_ScrollViewer",
            // Transparent, not null/unset — an unpainted area isn't hit-testable in Avalonia.
            // Once zoomed all the way out the content shrinks to a small block in the top-left
            // corner, leaving most of the viewport with nothing rendered there; without an
            // explicit background that dead space swallows touches instead of routing them to
            // our pinch handlers, making it look like zooming back in had stopped working.
            Background = Brushes.Transparent,
            Content = zoomHost,
        };
        scrollViewer.RegisterInNameScope(scope);
        scrollViewer[!ScrollViewer.HorizontalScrollBarVisibilityProperty] =
            control[!HorizontalScrollBarVisibilityProperty];
        scrollViewer[!ScrollViewer.VerticalScrollBarVisibilityProperty] =
            control[!VerticalScrollBarVisibilityProperty];

        return scrollViewer;
    });

    static PanZoomView()
    {
        TemplateProperty.OverrideDefaultValue<PanZoomView>(DefaultTemplate);
        ZoomProperty.Changed.AddClassHandler<PanZoomView>((view, e) => view.OnZoomPropertyChanged((double)e.NewValue!));
    }

    private ScrollViewer? _scrollViewer;
    private ZoomHostPanel? _zoomHost;
    private ContentPresenter? _contentPresenter;
    private readonly ScaleTransform _zoomTransform = new();

    // The zoom level we last actually rendered at — distinct from the Zoom property so we can
    // tell "did this change come from our own ApplyZoomCore" apart from "did someone else set
    // Zoom externally" without re-entrancy.
    private double _appliedZoom = 1.0;

    // Manual touch-pinch tracking, keyed by pointer identity — deliberately not using
    // Avalonia's PinchGestureRecognizer; see the class doc comment for why.
    private readonly Dictionary<IPointer, Point> _activeTouches = new();
    private double _touchPinchStartDistance;
    private double _touchPinchStartScale = 1.0;

    // Pointers we've captured away from the ScrollViewer's own pan recognizer for a pinch.
    // Once a finger drops out of a 2-finger pinch back to 1, that recognizer can't resume
    // tracking it — resuming needs a fresh PointerPressed, which only fires once per
    // touch-down, and this finger is already mid-touch. So for the rest of that finger's
    // touch-down we drive panning ourselves in OnTouchPointerMoved instead of handing capture
    // back to a recognizer with no way to pick it back up.
    private readonly HashSet<IPointer> _capturedForPinch = new();

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        _zoomHost = e.NameScope.Find<ZoomHostPanel>("PART_ZoomHost");
        _contentPresenter = e.NameScope.Find<ContentPresenter>("PART_ContentPresenter");
        if (_contentPresenter != null)
            _contentPresenter.RenderTransform = _zoomTransform;

        if (_scrollViewer == null) return;

        _scrollViewer.AddHandler(PointerPressedEvent, OnTouchPointerPressed, handledEventsToo: true);
        _scrollViewer.AddHandler(PointerMovedEvent, OnTouchPointerMoved, handledEventsToo: true);
        _scrollViewer.AddHandler(PointerReleasedEvent, OnTouchPointerReleased, handledEventsToo: true);
        _scrollViewer.AddHandler(PointerCaptureLostEvent, OnTouchPointerCaptureLost, handledEventsToo: true);
        _scrollViewer.AddHandler(PointerWheelChangedEvent, OnWheelChanged, handledEventsToo: true);
    }

    private void OnZoomPropertyChanged(double newValue)
    {
        if (_scrollViewer == null) return;
        if (Math.Abs(newValue - _appliedZoom) < 0.0001) return;

        var anchor = new Point(_scrollViewer.Viewport.Width / 2, _scrollViewer.Viewport.Height / 2);
        ApplyZoomCore(newValue, anchor);
    }

    // ── Wheel zoom ───────────────────────────────────────────────────────────

    private void OnWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_scrollViewer == null || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        var delta = e.Delta.Y * WheelZoomStep;
        ApplyZoomCore(_appliedZoom + delta, e.GetPosition(_scrollViewer));
        e.Handled = true;
    }

    // ── Touch pinch ──────────────────────────────────────────────────────────

    private void OnTouchPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_scrollViewer == null || e.Pointer.Type != PointerType.Touch) return;

        _activeTouches[e.Pointer] = e.GetPosition(_scrollViewer);
        if (_activeTouches.Count == 2)
        {
            // A second finger means this is a pinch, not a pan. Capture both pointers to the
            // ScrollViewer itself so the pointer route no longer reaches its inner
            // ScrollContentPresenter (and that part's own pan recognizer) for these two
            // pointers — otherwise the built-in pan keeps dragging the scroll offset off the
            // two fingers' average movement at the same time we're zooming, so the view jumps
            // around mid-pinch instead of just zooming in place.
            foreach (var pointer in _activeTouches.Keys)
            {
                pointer.Capture(_scrollViewer);
                _capturedForPinch.Add(pointer);
            }

            var pts = _activeTouches.Values.ToArray();
            _touchPinchStartDistance = Distance(pts[0], pts[1]);
            _touchPinchStartScale = _appliedZoom;
            Console.WriteLine($"[PanZoom] pinch START dist={_touchPinchStartDistance:F1} scale={_touchPinchStartScale:F3}");
        }
    }

    private void OnTouchPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_scrollViewer == null || !_activeTouches.ContainsKey(e.Pointer)) return;

        var previous = _activeTouches[e.Pointer];
        var current = e.GetPosition(_scrollViewer);
        _activeTouches[e.Pointer] = current;

        if (_activeTouches.Count == 2)
        {
            if (_touchPinchStartDistance <= 0)
            {
                Console.WriteLine("[PanZoom] pinch MOVE skipped: no start distance baseline");
                return;
            }
            var pts = _activeTouches.Values.ToArray();
            var distance = Distance(pts[0], pts[1]);
            var origin = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
            var target = _touchPinchStartScale * (distance / _touchPinchStartDistance);
            Console.WriteLine($"[PanZoom] pinch MOVE dist={distance:F1} target={target:F3} appliedZoom={_appliedZoom:F3} min={MinZoom:F3} max={MaxZoom:F3}");
            ApplyZoomCore(target, origin);
            return;
        }

        // A leftover single finger from a pinch that just dropped to 1 contact — the built-in
        // recognizer can't take it back mid-touch (see _capturedForPinch), so pan it ourselves.
        if (_activeTouches.Count == 1 && _capturedForPinch.Contains(e.Pointer))
        {
            var maxX = Math.Max(0, _scrollViewer.Extent.Width - _scrollViewer.Viewport.Width);
            var maxY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
            _scrollViewer.Offset = new Vector(
                Math.Clamp(_scrollViewer.Offset.X - (current.X - previous.X), 0, maxX),
                Math.Clamp(_scrollViewer.Offset.Y - (current.Y - previous.Y), 0, maxY));
        }
    }

    private void OnTouchPointerReleased(object? sender, PointerReleasedEventArgs e) => EndTouch(e.Pointer);

    private void OnTouchPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndTouch(e.Pointer);

    private void EndTouch(IPointer pointer)
    {
        _activeTouches.Remove(pointer);
        _capturedForPinch.Remove(pointer);
        // Below 2 contacts there's no pinch baseline to continue from — force a fresh
        // distance reading if/when a second finger rejoins, rather than reusing a stale one.
        if (_activeTouches.Count < 2) _touchPinchStartDistance = 0;
        Console.WriteLine($"[PanZoom] touch END, remaining={_activeTouches.Count}");
    }

    private static double Distance(Point a, Point b) => new Vector(a.X - b.X, a.Y - b.Y).Length;

    // ── Core zoom application ───────────────────────────────────────────────

    // Zooms so that the content point under `anchorInViewport` stays fixed on screen. Uses a
    // RenderTransform (cheap, no remeasure of the content's own subtree) plus PART_ZoomHost
    // (a ZoomHostPanel — see its doc comment) reporting the scaled size upward, so the
    // ScrollViewer's extent/offset update lands in the same frame as the scale change rather
    // than lagging a layout pass behind.
    private void ApplyZoomCore(double targetScale, Point anchorInViewport)
    {
        if (_scrollViewer == null || _zoomHost == null || _contentPresenter == null) return;

        var newScale = Math.Clamp(targetScale, MinZoom, MaxZoom);
        if (Math.Abs(newScale - _appliedZoom) < 0.0001)
        {
            Console.WriteLine($"[PanZoom] ApplyZoomCore no-op: target={targetScale:F3} clamped={newScale:F3} appliedZoom={_appliedZoom:F3}");
            return;
        }

        // Force a fresh, unconstrained measurement right now rather than trusting whatever
        // DesiredSize is currently cached — see ZoomHostPanel's doc comment for why that
        // distinction matters.
        _contentPresenter.Measure(Size.Infinity);
        var natural = _contentPresenter.DesiredSize;
        if (natural.Width <= 0 || natural.Height <= 0)
        {
            Console.WriteLine($"[PanZoom] ApplyZoomCore ABORTED: natural size invalid ({natural.Width:F1}x{natural.Height:F1})");
            return;
        }

        var offset = _scrollViewer.Offset;
        var contentX = anchorInViewport.X + offset.X;
        var contentY = anchorInViewport.Y + offset.Y;
        var ratio = newScale / _appliedZoom;

        _appliedZoom = newScale;
        _zoomTransform.ScaleX = newScale;
        _zoomTransform.ScaleY = newScale;
        _zoomHost.Scale = newScale;
        _zoomHost.InvalidateMeasure();

        var scaledWidth = natural.Width * newScale;
        var scaledHeight = natural.Height * newScale;
        var maxOffsetX = Math.Max(0, scaledWidth - _scrollViewer.Viewport.Width);
        var maxOffsetY = Math.Max(0, scaledHeight - _scrollViewer.Viewport.Height);

        _scrollViewer.Offset = new Vector(
            Math.Clamp(contentX * ratio - anchorInViewport.X, 0, maxOffsetX),
            Math.Clamp(contentY * ratio - anchorInViewport.Y, 0, maxOffsetY));

        SetCurrentValue(ZoomProperty, newScale);
    }
}
