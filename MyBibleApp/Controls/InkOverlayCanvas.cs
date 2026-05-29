using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MyBibleApp.Models;
using SkiaSharp;

namespace MyBibleApp.Controls;

public sealed class InkStrokeEventArgs : EventArgs
{
    public required string StrokeId { get; init; }
    public required IReadOnlyList<Point> Points { get; init; }
    public required Color Color { get; init; }
    public required double StrokeWidth { get; init; }
    public required bool IsHighlight { get; init; }
    public required int AnchorParagraphIndex { get; init; }
    public required double AnchorContentTop { get; init; }
}

/// <summary>Carries IDs of strokes removed by undo or the eraser tool.</summary>
public sealed class InkStrokeRemovedEventArgs(IReadOnlyList<string> strokeIds) : EventArgs
{
    public IReadOnlyList<string> StrokeIds { get; } = strokeIds;
}

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
        Point DotCenter,            // used only when Points is null
        Rect ContentBounds,         // content-space AABB for culling / eraser
        Color Color,                // per-stroke ink colour
        double StrokeWidth,         // pen width used when drawing this stroke
        bool IsHighlight,           // true → drawn with multiply blend
        IReadOnlyList<Point>? Points, // raw points for eraser hit-testing (null for dots)
        int AnchorParagraphIndex = -1,   // index into the paragraph list at draw time
        double AnchorContentTop = 0,     // content-space Y of that paragraph's top at draw time
        string StrokeId = "");           // stable ID linking cache entry to journal store

    private readonly List<StrokeCache> _cachedStrokes = new();
    private readonly Stack<StrokeCache> _redoStack = new();

    // Active stroke raw points (content-space).
    private List<Point>? _activeStroke;
    private Color _activeStrokeColor;
    private double _activeStrokeWidth;
    private bool _activeIsHighlight;
    private int _activeAnchorIndex = -1;
    private double _activeAnchorContentTop;

    private double _scrollOffsetY;
    private double _textColumnOffsetX;

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

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler<InkStrokeEventArgs>? StrokeCompleted;
    /// <summary>Fired when one or more strokes are removed (undo or eraser).</summary>
    public event EventHandler<InkStrokeRemovedEventArgs>? StrokeRemoved;

    // ── Public API called by MainView ─────────────────────────────────────────

    /// <summary>Call when the ListBox ScrollViewer offset changes.</summary>
    public void UpdateScrollOffset(double offsetY)
    {
        if (Math.Abs(offsetY - _scrollOffsetY) < 0.5) return;
        _scrollOffsetY = offsetY;
        InvalidateVisual();
    }

    /// <summary>
    /// Call when the text column's X position within this canvas changes (e.g. journal layout
    /// applied, window resized). Strokes are stored in column-relative X coordinates so they
    /// stay aligned regardless of screen width; this offset converts between the two spaces.
    /// </summary>
    public void UpdateTextColumnOffsetX(double offsetX)
    {
        if (Math.Abs(offsetX - _textColumnOffsetX) < 0.5) return;
        _textColumnOffsetX = offsetX;
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
        InvalidateVisual();
    }

    /// <summary>Finish the current stroke. No-op in eraser mode.</summary>
    public void EndStroke()
    {
        if (IsEraserMode) return;
        if (_activeStroke != null && _activeStroke.Count > 0)
        {
            _redoStack.Clear();
            var id = Guid.NewGuid().ToString();
            if (_activeStroke.Count == 1)
            {
                var p = _activeStroke[0];
                _cachedStrokes.Add(new StrokeCache(
                    p,
                    new Rect(p.X - 2, p.Y - 2, 4, 4),
                    _activeStrokeColor, _activeStrokeWidth, _activeIsHighlight, null,
                    _activeAnchorIndex, _activeAnchorContentTop, id));
                StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
                {
                    StrokeId = id,
                    Points = [],
                    Color = _activeStrokeColor,
                    StrokeWidth = _activeStrokeWidth,
                    IsHighlight = _activeIsHighlight,
                    AnchorParagraphIndex = _activeAnchorIndex,
                    AnchorContentTop = _activeAnchorContentTop
                });
            }
            else
            {
                var pts = _activeStroke.AsReadOnly();
                _cachedStrokes.Add(new StrokeCache(
                    default,
                    ComputeBounds(_activeStroke),
                    _activeStrokeColor,
                    _activeStrokeWidth,
                    _activeIsHighlight,
                    pts,
                    _activeAnchorIndex,
                    _activeAnchorContentTop,
                    id));
                StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
                {
                    StrokeId = id,
                    Points = pts,
                    Color = _activeStrokeColor,
                    StrokeWidth = _activeStrokeWidth,
                    IsHighlight = _activeIsHighlight,
                    AnchorParagraphIndex = _activeAnchorIndex,
                    AnchorContentTop = _activeAnchorContentTop
                });
            }
        }
        _activeStroke = null;
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
        _redoStack.Clear();
        if (state != null)
            _cachedStrokes.AddRange(state.Strokes);
        _activeStroke = null;
        InvalidateVisual();
    }

    /// <summary>Erase all strokes.</summary>
    public void ClearStrokes()
    {
        _cachedStrokes.Clear();
        _redoStack.Clear();
        _activeStroke = null;
        InvalidateVisual();
    }

    /// <summary>Load strokes from a persisted journal, replacing any existing strokes.</summary>
    public void LoadJournalStrokes(IReadOnlyList<JournalInkStroke> strokes)
    {
        _cachedStrokes.Clear();
        _redoStack.Clear();
        foreach (var stroke in strokes)
        {
            var pts = stroke.Points.Select(p => new Point(p.X, p.Y)).ToList();
            var color = Color.Parse(stroke.Color.Length > 0 ? stroke.Color : "#FF000000");

            if (pts.Count == 0)
                continue;

            if (pts.Count == 1)
            {
                var p = pts[0];
                _cachedStrokes.Add(new StrokeCache(
                    p,
                    new Rect(p.X - 2, p.Y - 2, 4, 4),
                    color, stroke.StrokeWidth, stroke.IsHighlight, null,
                    stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id));
            }
            else
            {
                _cachedStrokes.Add(new StrokeCache(
                    default,
                    ComputeBounds(pts),
                    color, stroke.StrokeWidth, stroke.IsHighlight,
                    pts,
                    stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id));
            }
        }
        InvalidateVisual();
    }

    /// <summary>Remove the most recently completed stroke, pushing it onto the redo stack.</summary>
    public void UndoStroke()
    {
        if (_cachedStrokes.Count == 0) return;
        var removed = _cachedStrokes[_cachedStrokes.Count - 1];
        _cachedStrokes.RemoveAt(_cachedStrokes.Count - 1);
        _redoStack.Push(removed);
        InvalidateVisual();
        if (!string.IsNullOrEmpty(removed.StrokeId))
            StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs([removed.StrokeId]));
    }

    /// <summary>Re-apply the most recently undone stroke.</summary>
    public void RedoStroke()
    {
        if (_redoStack.Count == 0) return;
        var stroke = _redoStack.Pop();
        _cachedStrokes.Add(stroke);
        InvalidateVisual();
        if (!string.IsNullOrEmpty(stroke.StrokeId))
        {
            var pts = stroke.Points ?? (IReadOnlyList<Point>)[];
            StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
            {
                StrokeId = stroke.StrokeId,
                Points = pts,
                Color = stroke.Color,
                StrokeWidth = stroke.StrokeWidth,
                IsHighlight = stroke.IsHighlight,
                AnchorParagraphIndex = stroke.AnchorParagraphIndex,
                AnchorContentTop = stroke.AnchorContentTop
            });
        }
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
        base.OnPointerCaptureLost(e);
    }

    // ── Eraser ────────────────────────────────────────────────────────────────

    private void EraseAt(Point contentPoint)
    {
        const double radius   = 14.0;
        const double radiusSq = radius * radius;
        List<string>? removedIds = null;

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
            if (s.Points is null)
            {
                var dx = s.DotCenter.X - adjustedPoint.X;
                var dy = s.DotCenter.Y - adjustedPoint.Y;
                if (dx * dx + dy * dy <= radiusSq)
                {
                    if (!string.IsNullOrEmpty(s.StrokeId))
                        (removedIds ??= []).Add(s.StrokeId);
                    _cachedStrokes.RemoveAt(i);
                }
                continue;
            }

            // Multi-point stroke – hit if eraser circle intersects any segment.
            bool hit = false;
            if (s.Points.Count == 1)
            {
                var dx = s.Points[0].X - adjustedPoint.X;
                var dy = s.Points[0].Y - adjustedPoint.Y;
                hit = dx * dx + dy * dy <= radiusSq;
            }
            else
            {
                for (int j = 0; j < s.Points.Count - 1 && !hit; j++)
                    hit = DistToSegmentSq(adjustedPoint, s.Points[j], s.Points[j + 1]) <= radiusSq;
            }
            if (hit)
            {
                if (!string.IsNullOrEmpty(s.StrokeId))
                    (removedIds ??= []).Add(s.StrokeId);
                _cachedStrokes.RemoveAt(i);
            }
        }

        if (removedIds != null)
        {
            _redoStack.Clear();
            InvalidateVisual();
            StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(removedIds));
        }
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

        using var clip = context.PushClip(new Rect(Bounds.Size));

        double viewTop    = _scrollOffsetY - 2000;
        double viewBottom = _scrollOffsetY + Bounds.Height + 2000;

        List<(StrokeCache Stroke, double DriftDelta)>? highlightStrokes = null;
        List<(StrokeCache Stroke, double DriftDelta)>? penStrokes = null;

        foreach (var s in _cachedStrokes)
        {
            var delta = GetDriftDelta(s.AnchorParagraphIndex, s.AnchorContentTop);
            var top   = s.ContentBounds.Top    + delta;
            var bot   = s.ContentBounds.Bottom + delta;
            if (bot < viewTop || top > viewBottom) continue;

            if (s.IsHighlight)
                (highlightStrokes ??= new()).Add((s, delta));
            else
                (penStrokes ??= new()).Add((s, delta));
        }

        StrokeCache? activeHighlight = null;
        double activeHighlightDelta  = 0;
        StrokeCache? activePen       = null;
        double activePenDelta        = 0;

        if (_activeStroke != null && _activeStroke.Count > 0)
        {
            var pts = _activeStroke.AsReadOnly();
            if (_activeIsHighlight)
            {
                activeHighlight      = new StrokeCache(default, default, _activeStrokeColor, _activeStrokeWidth, true, pts, _activeAnchorIndex, _activeAnchorContentTop);
                activeHighlightDelta = GetDriftDelta(_activeAnchorIndex, _activeAnchorContentTop);
            }
            else
            {
                activePen      = new StrokeCache(default, default, _activeStrokeColor, _activeStrokeWidth, false, pts, _activeAnchorIndex, _activeAnchorContentTop);
                activePenDelta = GetDriftDelta(_activeAnchorIndex, _activeAnchorContentTop);
            }
        }

        if (highlightStrokes != null || penStrokes != null || activeHighlight.HasValue || activePen.HasValue)
            context.Custom(new SkiaInkDrawOperation(
                new Rect(Bounds.Size),
                highlightStrokes, penStrokes,
                activeHighlight, activeHighlightDelta,
                activePen, activePenDelta,
                _scrollOffsetY, _textColumnOffsetX));
    }

    // ── Skia ink draw operation (Multiply blend for both highlights and pen) ─────

    private sealed class SkiaInkDrawOperation : ICustomDrawOperation
    {
        private readonly IReadOnlyList<(StrokeCache Stroke, double DriftDelta)>? _highlightStrokes;
        private readonly IReadOnlyList<(StrokeCache Stroke, double DriftDelta)>? _penStrokes;
        private readonly StrokeCache? _activeHighlight;
        private readonly double _activeHighlightDelta;
        private readonly StrokeCache? _activePen;
        private readonly double _activePenDelta;
        private readonly double _scrollOffsetY;
        private readonly double _textColumnOffsetX;

        public Rect Bounds { get; }

        public SkiaInkDrawOperation(
            Rect bounds,
            IReadOnlyList<(StrokeCache Stroke, double DriftDelta)>? highlightStrokes,
            IReadOnlyList<(StrokeCache Stroke, double DriftDelta)>? penStrokes,
            StrokeCache? activeHighlight, double activeHighlightDelta,
            StrokeCache? activePen, double activePenDelta,
            double scrollOffsetY, double textColumnOffsetX)
        {
            Bounds                = bounds;
            _highlightStrokes     = highlightStrokes;
            _penStrokes           = penStrokes;
            _activeHighlight      = activeHighlight;
            _activeHighlightDelta = activeHighlightDelta;
            _activePen            = activePen;
            _activePenDelta       = activePenDelta;
            _scrollOffsetY        = scrollOffsetY;
            _textColumnOffsetX    = textColumnOffsetX;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
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

            // Highlights first (alpha=128), pen strokes on top (stroke's own alpha).
            if (_highlightStrokes != null)
                foreach (var (stroke, delta) in _highlightStrokes)
                {
                    canvas.Save();
                    canvas.Translate((float)_textColumnOffsetX, (float)(-_scrollOffsetY + delta));
                    DrawStroke(canvas, paint, stroke, isHighlight: true);
                    canvas.Restore();
                }

            if (_activeHighlight.HasValue)
            {
                canvas.Save();
                canvas.Translate((float)_textColumnOffsetX, (float)(-_scrollOffsetY + _activeHighlightDelta));
                DrawStroke(canvas, paint, _activeHighlight.Value, isHighlight: true);
                canvas.Restore();
            }

            if (_penStrokes != null)
                foreach (var (stroke, delta) in _penStrokes)
                {
                    canvas.Save();
                    canvas.Translate((float)_textColumnOffsetX, (float)(-_scrollOffsetY + delta));
                    DrawStroke(canvas, paint, stroke, isHighlight: false);
                    canvas.Restore();
                }

            if (_activePen.HasValue)
            {
                canvas.Save();
                canvas.Translate((float)_textColumnOffsetX, (float)(-_scrollOffsetY + _activePenDelta));
                DrawStroke(canvas, paint, _activePen.Value, isHighlight: false);
                canvas.Restore();
            }

            canvas.Restore();
        }

        private static void DrawStroke(SKCanvas canvas, SKPaint paint, StrokeCache stroke, bool isHighlight)
        {
            var alpha         = isHighlight ? (byte)128 : stroke.Color.A;
            paint.Color       = new SKColor(stroke.Color.R, stroke.Color.G, stroke.Color.B, alpha);
            paint.StrokeWidth = (float)stroke.StrokeWidth;

            var pts = stroke.Points;

            if (pts == null)
            {
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle((float)stroke.DotCenter.X, (float)stroke.DotCenter.Y,
                    (float)(stroke.StrokeWidth / 2), paint);
                paint.Style = SKPaintStyle.Stroke;
                return;
            }

            if (pts.Count == 1)
            {
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle((float)pts[0].X, (float)pts[0].Y,
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

    internal static double DistToSegmentSq(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-10)
        {
            dx = p.X - a.X; dy = p.Y - a.Y;
            return dx * dx + dy * dy;
        }
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0.0, 1.0);
        dx = p.X - (a.X + t * dx);
        dy = p.Y - (a.Y + t * dy);
        return dx * dx + dy * dy;
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

    /// <summary>
    /// Convert a viewport point to content space.
    /// Y becomes scroll-relative; X becomes text-column-relative (negative = left margin).
    /// </summary>
    private Point ToContent(Point viewport) =>
        new(viewport.X - _textColumnOffsetX, viewport.Y + _scrollOffsetY);
}
