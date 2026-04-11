using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using MyBibleApp.Models;

namespace MyBibleApp.Controls;

// Basic pen-only ink layer that stays attached to each paragraph while scrolling.
public class ParagraphInkCanvas : Control
{
    private BibleInkStroke? _activeInkStroke;
    private bool _isInking;
    // Discovered at attach-time; kept so we can unsubscribe on detach.
    private ToggleSwitch? _annotationToggle;

    // Nearly-transparent brush: visually invisible but creates a composition hit area
    // so pointer events reach this control when annotation mode is active.
    private static readonly IBrush HitAreaBrush = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));

    public static readonly StyledProperty<IList<BibleInkStroke>?> InkStrokesProperty =
        AvaloniaProperty.Register<ParagraphInkCanvas, IList<BibleInkStroke>?>(nameof(InkStrokes));

    /// <summary>
    /// When true the canvas is hit-testable and pen input will draw ink.
    /// When false the canvas is invisible to hit-testing so touch/scroll and text-selection pass through.
    /// </summary>
    public static readonly StyledProperty<bool> IsAnnotatingProperty =
        AvaloniaProperty.Register<ParagraphInkCanvas, bool>(nameof(IsAnnotating), defaultValue: false);

    public IList<BibleInkStroke>? InkStrokes
    {
        get => GetValue(InkStrokesProperty);
        set => SetValue(InkStrokesProperty, value);
    }

    public bool IsAnnotating
    {
        get => GetValue(IsAnnotatingProperty);
        set => SetValue(IsAnnotatingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsAnnotatingProperty)
        {
            // Re-render so the hit-area brush is added/removed from the composition layer.
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // When annotation is active, fill the bounds with a near-transparent brush.
        // Avalonia 12's composition hit tester requires rendered content to consider
        // a control hittable — without this, pointer events fall through to controls below.
        if (IsAnnotating)
        {
            context.DrawRectangle(HitAreaBrush, null, new Rect(Bounds.Size));
        }

        var strokes = InkStrokes;
        if (strokes is null || strokes.Count == 0)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 193, 7)), 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        foreach (var stroke in strokes)
        {
            if (stroke.Points.Count == 0)
            {
                continue;
            }

            if (stroke.Points.Count == 1)
            {
                context.DrawEllipse(pen.Brush, null, stroke.Points[0], 1.5, 1.5);
                continue;
            }

            for (var i = 1; i < stroke.Points.Count; i++)
            {
                context.DrawLine(pen, stroke.Points[i - 1], stroke.Points[i]);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!ShouldStartInking(e))
        {
            base.OnPointerPressed(e);
            return;
        }

        var strokes = InkStrokes;
        if (strokes is null)
        {
            base.OnPointerPressed(e);
            return;
        }

        _isInking = true;
        _activeInkStroke = new BibleInkStroke();
        _activeInkStroke.Points.Add(e.GetPosition(this));
        strokes.Add(_activeInkStroke);

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
        // Do NOT call base — we own this event; calling base after Handled=true can
        // still trigger scroll-gesture recognizers on ancestor ScrollViewers.
    }

    private bool ShouldStartInking(PointerPressedEventArgs e)
    {
        // Only accept pen input for inking. This ensures:
        // - Touch events do not draw (they can only scroll via the MainView handlers)
        // - Mouse left-click does not draw
        // - Only actual stylus/pen input creates ink strokes
        return e.Pointer.Type == PointerType.Pen;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_isInking || _activeInkStroke is null)
        {
            base.OnPointerMoved(e);
            return;
        }

        _activeInkStroke.Points.Add(e.GetPosition(this));
        e.Handled = true;
        InvalidateVisual();
        // Do NOT call base — prevents the scroll recognizer from seeing the movement.
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isInking)
        {
            _isInking = false;
            _activeInkStroke = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _isInking = false;
        _activeInkStroke = null;
        base.OnPointerCaptureLost(e);
    }

    // -- Visual tree lifecycle -------------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DiscoverAnnotationToggle();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_annotationToggle != null)
        {
            _annotationToggle.IsCheckedChanged -= OnAnnotationToggleChanged;
            _annotationToggle = null;
        }
    }

    /// <summary>
    /// Walk up to the nearest ancestor UserControl, then search its visual
    /// descendants for the AnnotationToggle switch. This works correctly even
    /// when this canvas lives inside a DataTemplate.
    /// </summary>
    private void DiscoverAnnotationToggle()
    {
        // Unsubscribe from any previous toggle (e.g. re-attach scenario)
        if (_annotationToggle != null)
        {
            _annotationToggle.IsCheckedChanged -= OnAnnotationToggleChanged;
            _annotationToggle = null;
        }

        var ancestor = this.FindAncestorOfType<UserControl>();
        if (ancestor == null) return;

        var toggle = ancestor.GetVisualDescendants()
            .OfType<ToggleSwitch>()
            .FirstOrDefault(t => t.Name == "AnnotationToggle");

        if (toggle == null) return;

        _annotationToggle = toggle;
        IsAnnotating = toggle.IsChecked == true;
        toggle.IsCheckedChanged += OnAnnotationToggleChanged;
    }

    private void OnAnnotationToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
            IsAnnotating = toggle.IsChecked == true;
    }
}
