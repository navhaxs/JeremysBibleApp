using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace MyBibleApp.Controls;

/// <summary>
/// A single full-viewport ink canvas placed as a sibling of the ListBox.
/// It is IsHitTestVisible=false so touch events fall through to the ListBox for
/// scrolling. Pen events are routed here by MainView via pointer capture.
///
/// Strokes are stored in content-space coordinates (y + scrollOffset at time of
/// drawing) and rendered back to viewport space via a single PushTransform so they
/// appear to scroll along with the text.
///
/// Highlight strokes are composited with SKBlendMode.Multiply via a custom Skia draw
/// operation so they darken the underlying text rather than covering it.
/// Pen strokes use regular SourceOver rendering on top.
/// </summary>
public class InkOverlayCanvas : Control
{
    // ── Styled properties ─────────────────────────────────────────────────────

    public static readonly StyledProperty<Color> PenColorProperty =
        AvaloniaProperty.Register<InkOverlayCanvas, Color>(
            nameof(PenColor), Color.FromArgb(210, 255, 193, 7));

    public Color PenColor
    {
        get => GetValue(PenColorProperty);
        set => SetValue(PenColorProperty, value);
    }

    public static readonly StyledProperty<bool> IsEraserModeProperty =
        AvaloniaProperty.Register<InkOverlayCanvas, bool>(nameof(IsEraserMode), false);

    public bool IsEraserMode
    {
        get => GetValue(IsEraserModeProperty);
        set => SetValue(IsEraserModeProperty, value);
    }

    public static readonly StyledProperty<bool> IsHighlighterModeProperty =
        AvaloniaProperty.Register<InkOverlayCanvas, bool>(nameof(IsHighlighterMode), false);

    public bool IsHighlighterMode
    {
        get => GetValue(IsHighlighterModeProperty);
        set => SetValue(IsHighlighterModeProperty, value);
    }

    private const double PenStrokeWidth         = 2.5;
    private const double HighlighterStrokeWidth = 14.0;

    // ── Stroke cache ──────────────────────────────────────────────────────────

    // Represents a completed, immutable stroke.
    // AnchorParagraphIndex / AnchorContentTop let the renderer correct for
    // virtualizing-panel drift by comparing the anchor paragraph's recorded
    // content-top with its current content-top.
    internal readonly record struct StrokeCache(
        StreamGeometry? Geo,        // null → single-dot stroke
        Point DotCenter,            // used only when Geo is null
        Rect ContentBounds,         // content-space AABB for culling / eraser
        Color Color,                // per-stroke ink colour
        double StrokeWidth,         // pen width used when drawing this stroke
        bool IsHighlight,           // true → drawn with multiply blend
        IReadOnlyList<Point>? Points, // raw points for eraser hit-testing (null for dots)
        int AnchorParagraphIndex = -1,   // index into the paragraph list at draw time
        double AnchorContentTop = 0);    // content-space Y of that paragraph's top at draw time

    private readonly List<StrokeCache> _cachedStrokes = new();

    // Active stroke raw points (content-space) and its cached geometry.
    private List<Point>? _activeStroke;
    private StreamGeometry? _activeGeo;     // rebuilt lazily when _activeStrokeDirty
    private bool _activeStrokeDirty;
    private Color _activeStrokeColor;
    private double _activeStrokeWidth;
    private bool _activeIsHighlight;
    private int _activeAnchorIndex = -1;
    private double _activeAnchorContentTop;

    private double _scrollOffsetY;

    private readonly Dictionary<(Color, double), Pen> _penCache = new();

    // ── Paragraph position provider (set by MainView) ────────────────────────

    /// <summary>
    /// Given a content-space Y, returns the paragraph index and its content-top.
    /// Used to anchor strokes to paragraphs at draw time.
    /// </summary>
    public Func<double, (int Index, double ContentTop)?>? FindParagraphAtContentY { get; set; }

    /// <summary>
    /// Given a paragraph index, returns its current content-space top.
    /// Used at render time to correct for virtualizing-panel drift.
    /// </summary>
    public Func<int, double?>? GetParagraphContentTop { get; set; }

    // ── Public API called by MainView ─────────────────────────────────────────

    /// <summary>Call when the ListBox ScrollViewer offset changes.</summary>
    public void UpdateScrollOffset(double offsetY)
    {
        _scrollOffsetY = offsetY;
        InvalidateVisual();
    }

    /// <summary>Begin a new ink stroke (or erase) at the given viewport position.</summary>
    public void StartStroke(Point viewportPoint)
    {
        if (IsEraserMode)
        {
            EraseAt(ToContent(viewportPoint));
            return;
        }
        _activeStrokeColor = PenColor;
        _activeStrokeWidth  = IsHighlighterMode ? HighlighterStrokeWidth : PenStrokeWidth;
        _activeIsHighlight  = IsHighlighterMode;

        var contentPt = ToContent(viewportPoint);

        // Anchor to the nearest paragraph so strokes survive virtualizing-panel re-layout.
        var anchor = FindParagraphAtContentY?.Invoke(contentPt.Y);
        _activeAnchorIndex = anchor?.Index ?? -1;
        _activeAnchorContentTop = anchor?.ContentTop ?? 0;

        _activeStroke = new List<Point> { contentPt };
        _activeGeo = null;
        _activeStrokeDirty = true;
        InvalidateVisual();
    }

    /// <summary>Add a point to the active stroke (or continue erasing).</summary>
    public void ContinueStroke(Point viewportPoint)
    {
        if (IsEraserMode)
        {
            EraseAt(ToContent(viewportPoint));
            return;
        }
        if (_activeStroke == null) return;
        _activeStroke.Add(ToContent(viewportPoint));
        _activeStrokeDirty = true;
        InvalidateVisual();
    }

    /// <summary>Finish the current stroke. No-op in eraser mode.</summary>
    public void EndStroke()
    {
        if (IsEraserMode) return;
        if (_activeStroke != null && _activeStroke.Count > 0)
        {
            if (_activeStroke.Count == 1)
            {
                var p = _activeStroke[0];
                _cachedStrokes.Add(new StrokeCache(
                    null, p,
                    new Rect(p.X - 2, p.Y - 2, 4, 4),
                    _activeStrokeColor, _activeStrokeWidth, _activeIsHighlight, null,
                    _activeAnchorIndex, _activeAnchorContentTop));
            }
            else
            {
                var pts = _activeStroke.AsReadOnly();
                _cachedStrokes.Add(new StrokeCache(
                    BuildGeometry(_activeStroke),
                    default,
                    ComputeBounds(_activeStroke),
                    _activeStrokeColor,
                    _activeStrokeWidth,
                    _activeIsHighlight,
                    pts,
                    _activeAnchorIndex,
                    _activeAnchorContentTop));
            }
        }
        _activeStroke = null;
        _activeGeo = null;
        _activeStrokeDirty = false;
        _activeAnchorIndex = -1;
        _activeAnchorContentTop = 0;
        InvalidateVisual();
    }

    // ── Per-tab ink state snapshot ────────────────────────────────────────────

    /// <summary>Opaque snapshot of all completed strokes for a single tab.</summary>
    public sealed class InkState
    {
        internal IReadOnlyList<StrokeCache> Strokes { get; }
        internal InkState(List<StrokeCache> strokes) =>
            Strokes = new List<StrokeCache>(strokes);
    }

    /// <summary>Captures current completed strokes so they can be restored later.</summary>
    public InkState CaptureState() => new InkState(_cachedStrokes);

    /// <summary>Replaces the stroke list with a previously captured snapshot.</summary>
    public void RestoreState(InkState? state)
    {
        _cachedStrokes.Clear();
        if (state != null)
            _cachedStrokes.AddRange(state.Strokes);
        _activeStroke = null;
        _activeGeo = null;
        _activeStrokeDirty = false;
        InvalidateVisual();
    }

    /// <summary>Erase all strokes.</summary>
    public void ClearStrokes()
    {
        _cachedStrokes.Clear();
        _activeStroke = null;
        _activeGeo = null;
        _activeStrokeDirty = false;
        InvalidateVisual();
    }

    /// <summary>Remove the most recently completed stroke.</summary>
    public void UndoStroke()
    {
        if (_cachedStrokes.Count == 0) return;
        _cachedStrokes.RemoveAt(_cachedStrokes.Count - 1);
        InvalidateVisual();
    }

    // ── Pointer events (fired when MainView gives us pointer capture) ─────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Type != PointerType.Pen) return;

        if (IsEraserMode)
        {
            EraseAt(ToContent(e.GetPosition(this)));
            e.Handled = true;
        }
        else if (_activeStroke != null)
        {
            ContinueStroke(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Type != PointerType.Pen) return;

        EndStroke();              // no-op in eraser mode
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _activeStroke = null;
        _activeGeo = null;
        base.OnPointerCaptureLost(e);
    }

    // ── Eraser ────────────────────────────────────────────────────────────────

    private void EraseAt(Point contentPoint)
    {
        const double radius   = 14.0;
        const double radiusSq = radius * radius;
        bool changed = false;

        for (int i = _cachedStrokes.Count - 1; i >= 0; i--)
        {
            var s = _cachedStrokes[i];
            var delta = GetDriftDelta(s.AnchorParagraphIndex, s.AnchorContentTop);
            // Adjust the erase point into the stroke's original coordinate space.
            var adjustedPoint = new Point(contentPoint.X, contentPoint.Y - delta);

            // Quick AABB reject expanded by eraser radius.
            if (s.ContentBounds.Right  + radius < adjustedPoint.X ||
                s.ContentBounds.Left   - radius > adjustedPoint.X ||
                s.ContentBounds.Bottom + radius < adjustedPoint.Y ||
                s.ContentBounds.Top    - radius > adjustedPoint.Y)
                continue;

            // Single-dot stroke.
            if (s.Geo is null)
            {
                var dx = s.DotCenter.X - adjustedPoint.X;
                var dy = s.DotCenter.Y - adjustedPoint.Y;
                if (dx * dx + dy * dy <= radiusSq)
                {
                    _cachedStrokes.RemoveAt(i);
                    changed = true;
                }
                continue;
            }

            // Multi-point stroke – hit if any recorded point is within eraser radius.
            if (s.Points is null) continue;
            bool hit = false;
            foreach (var p in s.Points)
            {
                var dx = p.X - adjustedPoint.X;
                var dy = p.Y - adjustedPoint.Y;
                if (dx * dx + dy * dy <= radiusSq) { hit = true; break; }
            }
            if (hit)
            {
                _cachedStrokes.RemoveAt(i);
                changed = true;
            }
        }

        if (changed) InvalidateVisual();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute drift delta for a stroke's anchor paragraph. Returns 0 if no
    /// anchor is available or the paragraph position can't be resolved.
    /// </summary>
    private double GetDriftDelta(int anchorIndex, double anchorContentTop)
    {
        if (anchorIndex < 0 || GetParagraphContentTop == null) return 0;
        var currentTop = GetParagraphContentTop(anchorIndex);
        return currentTop.HasValue ? currentTop.Value - anchorContentTop : 0;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_cachedStrokes.Count == 0 && _activeStroke == null) return;

        // Clip to the visible viewport so strokes don't render outside our bounds.
        using var clip = context.PushClip(new Rect(Bounds.Size));

        // Use a generous view range to account for drift-corrected strokes that
        // shift into/out of the naive range.
        double viewTop    = _scrollOffsetY - 2000;
        double viewBottom = _scrollOffsetY + Bounds.Height + 2000;

        // ── Highlight strokes: rendered first with Skia Multiply blend ────────
        // Collect visible highlight strokes for the custom Skia draw operation.
        List<(StrokeCache Stroke, double DriftDelta)>? highlightStrokes = null;
        foreach (var s in _cachedStrokes)
        {
            if (!s.IsHighlight) continue;
            var delta = GetDriftDelta(s.AnchorParagraphIndex, s.AnchorContentTop);
            var adjustedTop = s.ContentBounds.Top + delta;
            var adjustedBottom = s.ContentBounds.Bottom + delta;
            if (adjustedBottom >= viewTop && adjustedTop <= viewBottom)
                (highlightStrokes ??= new()).Add((s, delta));
        }

        // Include an in-progress highlight stroke if one is active.
        StrokeCache? activeHighlight = (_activeStroke != null && _activeIsHighlight)
            ? new StrokeCache(null, default, default, _activeStrokeColor, _activeStrokeWidth, true,
                              _activeStroke.AsReadOnly(),
                              _activeAnchorIndex, _activeAnchorContentTop)
            : null;
        double activeHighlightDelta = (_activeStroke != null && _activeIsHighlight)
            ? GetDriftDelta(_activeAnchorIndex, _activeAnchorContentTop) : 0;

        if (highlightStrokes?.Count > 0 || activeHighlight.HasValue)
            context.Custom(new HighlightDrawOperation(
                new Rect(Bounds.Size), highlightStrokes, activeHighlight, activeHighlightDelta, _scrollOffsetY));

        // ── Pen strokes: regular SourceOver rendering on top of highlights ────
        foreach (var stroke in _cachedStrokes)
        {
            if (stroke.IsHighlight) continue;
            var delta = GetDriftDelta(stroke.AnchorParagraphIndex, stroke.AnchorContentTop);
            var adjustedTop = stroke.ContentBounds.Top + delta;
            var adjustedBottom = stroke.ContentBounds.Bottom + delta;
            if (adjustedBottom < _scrollOffsetY - 10 ||
                adjustedTop    > _scrollOffsetY + Bounds.Height + 10)
                continue;

            using var transform = context.PushTransform(
                Matrix.CreateTranslation(0, -_scrollOffsetY + delta));

            var pen   = GetPen(stroke.Color, stroke.StrokeWidth);
            var brush = pen.Brush!;

            if (stroke.Geo is null)
                context.DrawEllipse(brush, null, stroke.DotCenter, 1.5, 1.5);
            else
                context.DrawGeometry(null, pen, stroke.Geo);
        }

        if (_activeStroke != null && !_activeIsHighlight && _activeStroke.Count > 0)
        {
            var activeDelta = GetDriftDelta(_activeAnchorIndex, _activeAnchorContentTop);
            using var activeTransform = context.PushTransform(
                Matrix.CreateTranslation(0, -_scrollOffsetY + activeDelta));

            var activePen = GetPen(_activeStrokeColor, _activeStrokeWidth);
            if (_activeStroke.Count == 1)
            {
                context.DrawEllipse(activePen.Brush!, null, _activeStroke[0], 1.5, 1.5);
            }
            else
            {
                if (_activeStrokeDirty)
                {
                    _activeGeo = BuildGeometry(_activeStroke);
                    _activeStrokeDirty = false;
                }
                context.DrawGeometry(null, activePen, _activeGeo!);
            }
        }
    }

    // ── Highlight draw operation (Skia Multiply blend) ────────────────────────

    /// <summary>
    /// Custom draw operation that renders highlight strokes directly via the Skia
    /// canvas using SKBlendMode.Multiply so the highlight darkens the underlying
    /// Bible text rather than covering it.
    /// </summary>
    private sealed class HighlightDrawOperation : ICustomDrawOperation
    {
        private readonly IReadOnlyList<(StrokeCache Stroke, double DriftDelta)>? _strokes;
        private readonly StrokeCache? _activeStroke;
        private readonly double _activeDriftDelta;
        private readonly double _scrollOffsetY;

        public Rect Bounds { get; }

        public HighlightDrawOperation(
            Rect bounds,
            IReadOnlyList<(StrokeCache Stroke, double DriftDelta)>? strokes,
            StrokeCache? activeStroke,
            double activeDriftDelta,
            double scrollOffsetY)
        {
            Bounds             = bounds;
            _strokes           = strokes;
            _activeStroke      = activeStroke;
            _activeDriftDelta  = activeDriftDelta;
            _scrollOffsetY     = scrollOffsetY;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature == null) return;

            using var lease  = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));

            using var paint = new SKPaint
            {
                Style       = SKPaintStyle.Stroke,
                StrokeCap   = SKStrokeCap.Round,
                StrokeJoin  = SKStrokeJoin.Round,
                IsAntialias = true,
                BlendMode   = SKBlendMode.Multiply,
            };

            if (_strokes != null)
                foreach (var (stroke, delta) in _strokes)
                {
                    canvas.Save();
                    canvas.Translate(0, (float)(-_scrollOffsetY + delta));
                    DrawStroke(canvas, paint, stroke);
                    canvas.Restore();
                }

            if (_activeStroke.HasValue)
            {
                canvas.Save();
                canvas.Translate(0, (float)(-_scrollOffsetY + _activeDriftDelta));
                DrawStroke(canvas, paint, _activeStroke.Value);
                canvas.Restore();
            }

            canvas.Restore();
        }

        private static void DrawStroke(SKCanvas canvas, SKPaint paint, StrokeCache stroke)
        {
            // Force full opacity: multiply blend works correctly with opaque colours.
            // Semi-transparent stroke colours are made fully opaque here so the
            // multiply effect uses the full ink colour (yellow × text = readable text).
            paint.Color       = new SKColor(stroke.Color.R, stroke.Color.G, stroke.Color.B, 255);
            paint.StrokeWidth = (float)stroke.StrokeWidth;

            var pts = stroke.Points;

            if (pts == null)
            {
                // Completed single-tap dot (no points list).
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(
                    (float)stroke.DotCenter.X, (float)stroke.DotCenter.Y,
                    (float)(stroke.StrokeWidth / 2), paint);
                paint.Style = SKPaintStyle.Stroke;
                return;
            }

            if (pts.Count == 1)
            {
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(
                    (float)pts[0].X, (float)pts[0].Y,
                    (float)(stroke.StrokeWidth / 2), paint);
                paint.Style = SKPaintStyle.Stroke;
                return;
            }

            using var path = new SKPath();
            path.MoveTo((float)pts[0].X, (float)pts[0].Y);
            for (int i = 1; i < pts.Count; i++)
                path.LineTo((float)pts[i].X, (float)pts[i].Y);
            canvas.DrawPath(path, paint);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Pen GetPen(Color color, double width)
    {
        var key = (color, width);
        if (_penCache.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(new SolidColorBrush(color), width,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        _penCache[key] = pen;
        return pen;
    }

    private static StreamGeometry BuildGeometry(List<Point> points)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        ctx.BeginFigure(points[0], false);
        for (int i = 1; i < points.Count; i++)
            ctx.LineTo(points[i]);
        ctx.EndFigure(false);
        return geo;
    }

    private static Rect ComputeBounds(List<Point> points)
    {
        double minX = points[0].X, minY = points[0].Y;
        double maxX = minX, maxY = minY;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        // Add a small margin so the pen stroke thickness is fully included.
        return new Rect(minX - 5, minY - 5, maxX - minX + 10, maxY - minY + 10);
    }

    /// <summary>Convert a viewport point to content space (scroll-relative).</summary>
    private Point ToContent(Point viewport) =>
        new(viewport.X, viewport.Y + _scrollOffsetY);
}
