using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenBibleApp.Controls;

/// <summary>
/// A single full-viewport ink canvas placed as a sibling of the ListBox.
/// It is IsHitTestVisible=false so touch events fall through to the ListBox for
/// scrolling. Pen events are routed here by MainView via pointer capture.
///
/// Strokes are stored in content-space coordinates (y + scrollOffset at time of
/// drawing) and rendered back to viewport space (y - scrollOffset) so they appear
/// to scroll along with the text.
/// </summary>
public class InkOverlayCanvas : Control
{
    private static readonly IBrush StrokeBrush =
        new SolidColorBrush(Color.FromArgb(210, 255, 193, 7));
    private static readonly Pen StrokePen =
        new Pen(StrokeBrush, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private readonly List<List<Point>> _strokes = new();
    private List<Point>? _activeStroke;
    private double _scrollOffsetY;

    // ── Public API called by MainView ────────────────────────────────────────

    /// <summary>Call when the ListBox ScrollViewer offset changes.</summary>
    public void UpdateScrollOffset(double offsetY)
    {
        _scrollOffsetY = offsetY;
        InvalidateVisual();
    }

    /// <summary>Begin a new ink stroke at the given viewport position.</summary>
    public void StartStroke(Point viewportPoint)
    {
        _activeStroke = new List<Point> { ToContent(viewportPoint) };
        _strokes.Add(_activeStroke);
        InvalidateVisual();
    }

    /// <summary>Add a point to the active stroke.</summary>
    public void ContinueStroke(Point viewportPoint)
    {
        _activeStroke?.Add(ToContent(viewportPoint));
        InvalidateVisual();
    }

    /// <summary>Finish the current stroke.</summary>
    public void EndStroke()
    {
        _activeStroke = null;
        InvalidateVisual();
    }

    /// <summary>Erase all strokes.</summary>
    public void ClearStrokes()
    {
        _strokes.Clear();
        _activeStroke = null;
        InvalidateVisual();
    }

    // ── Pointer events (fired when MainView gives us pointer capture) ────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_activeStroke != null && e.Pointer.Type == PointerType.Pen)
        {
            ContinueStroke(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_activeStroke != null)
        {
            EndStroke();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _activeStroke = null;
        base.OnPointerCaptureLost(e);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_strokes.Count == 0) return;

        // Clip to the visible viewport so strokes don't render outside our bounds.
        using var _ = context.PushClip(new Rect(Bounds.Size));

        foreach (var stroke in _strokes)
        {
            if (stroke.Count == 0) continue;

            if (stroke.Count == 1)
            {
                var p = ToViewport(stroke[0]);
                if (InBounds(p))
                    context.DrawEllipse(StrokeBrush, null, p, 1.5, 1.5);
                continue;
            }

            for (int i = 1; i < stroke.Count; i++)
            {
                var p1 = ToViewport(stroke[i - 1]);
                var p2 = ToViewport(stroke[i]);
                context.DrawLine(StrokePen, p1, p2);
            }
        }
    }

    // ── Coordinate helpers ───────────────────────────────────────────────────

    /// <summary>Convert a viewport point to content space (scroll-relative).</summary>
    private Point ToContent(Point viewport) =>
        new(viewport.X, viewport.Y + _scrollOffsetY);

    /// <summary>Convert a content-space point back to viewport space for rendering.</summary>
    private Point ToViewport(Point content) =>
        new(content.X, content.Y - _scrollOffsetY);

    private bool InBounds(Point p) =>
        p.X >= -10 && p.Y >= -10 && p.X <= Bounds.Width + 10 && p.Y <= Bounds.Height + 10;
}


