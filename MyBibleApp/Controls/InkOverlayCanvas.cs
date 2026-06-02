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

/// <summary>Controls which stroke types an <see cref="InkOverlayCanvas"/> renders.</summary>
public enum InkDrawMode { All, PenOnly, HighlightOnly }

public sealed class InkStrokeEventArgs : EventArgs
{
    public required string StrokeId { get; init; }
    public required IReadOnlyList<Point> Points { get; init; }
    public required Color Color { get; init; }
    public required double StrokeWidth { get; init; }
    public required bool IsHighlight { get; init; }
    public required int AnchorParagraphIndex { get; init; }
    public required double AnchorContentTop { get; init; }
    public required int AnchorChapter { get; init; }
}

/// <summary>Carries strokes removed by undo or the eraser tool, with their chapter for store routing.</summary>
public sealed class InkStrokeRemovedEventArgs(IReadOnlyList<(string StrokeId, int Chapter)> removedStrokes) : EventArgs
{
    public IReadOnlyList<(string StrokeId, int Chapter)> RemovedStrokes { get; } = removedStrokes;

    // Convenience: just the IDs (for callers that don't care about chapter).
    public IReadOnlyList<string> StrokeIds { get; } =
        removedStrokes.Select(r => r.StrokeId).ToList();
}

/// <summary>
/// A full-viewport ink canvas placed as a sibling of the ListBox.
/// IsHitTestVisible=false so touch events fall through to the ListBox for scrolling.
/// Pen events are routed here by MainView via pointer capture.
///
/// Strokes are stored in content-space coordinates and rendered back to viewport space
/// so they scroll with the text.
///
/// Highlight strokes use SKBlendMode.Multiply (drawn above text). Pen strokes use
/// SKBlendMode.SrcOver and are rendered on a sibling canvas placed below the text layer.
/// Set <see cref="DrawMode"/> and <see cref="DataSource"/> to split the two layers.
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

    public static readonly StyledProperty<InkDrawMode> DrawModeProperty =
        AvaloniaProperty.Register<InkOverlayCanvas, InkDrawMode>(nameof(DrawMode), InkDrawMode.All);

    public InkDrawMode DrawMode
    {
        get => GetValue(DrawModeProperty);
        set => SetValue(DrawModeProperty, value);
    }

    /// <summary>
    /// When set, this canvas reads stroke data from the source canvas rather than its own store.
    /// Used by the pen-underlay canvas (DrawMode=PenOnly) to mirror the primary InkOverlay.
    /// </summary>
    public InkOverlayCanvas? DataSource { get; set; }

    /// <summary>Session-only flag: allow mouse pointer type to draw ink (for tablets where stylus reports as mouse).</summary>
    public bool AllowMouseInput { get; set; }

    private List<InkOverlayCanvas>? _renderSlaves;

    /// <summary>Register a slave canvas that should be invalidated whenever this canvas redraws.</summary>
    public void RegisterSlave(InkOverlayCanvas slave) =>
        (_renderSlaves ??= new()).Add(slave);

    private void Redraw()
    {
        InvalidateVisual();
        if (_renderSlaves != null)
            foreach (var s in _renderSlaves)
                s.InvalidateVisual();
    }

    // During active stroke drawing only the layer rendering that stroke type needs
    // updating. Highlights live on InkOverlay (self); pen strokes live on PenUnderlay
    // (slaves). This avoids redundant full re-renders on the other layer at 120Hz.
    private void RedrawActiveStrokeLayer()
    {
        if (_activeIsHighlight)
            InvalidateVisual();
        else if (_renderSlaves != null)
            foreach (var s in _renderSlaves)
                s.InvalidateVisual();
    }

    private const double PenStrokeWidth         = 2.5;
    private const double HighlighterStrokeWidth = 14.0;

    // ── Stroke cache ──────────────────────────────────────────────────────────

    // Represents a completed, immutable stroke.
    // AnchorChapter / AnchorParagraphIndex / AnchorContentTop let the renderer correct for
    // virtualizing-panel drift by comparing the anchor paragraph's recorded
    // content-top with its current content-top.
    internal readonly record struct StrokeCache(
        Point DotCenter,            // used only when Points is null
        Rect ContentBounds,         // content-space AABB for culling / eraser
        Color Color,                // per-stroke ink colour
        double StrokeWidth,         // pen width used when drawing this stroke
        bool IsHighlight,           // true → highlight (Multiply blend, above text); false → pen (SrcOver, below text)
        IReadOnlyList<Point>? Points, // raw points for eraser hit-testing (null for dots)
        int AnchorChapter = 0,           // 1-based chapter number; 0 = unanchored/legacy
        int AnchorParagraphIndex = -1,   // within-chapter paragraph index
        double AnchorContentTop = 0,     // content-space Y of that paragraph's top at draw time
        string StrokeId = "",            // stable ID linking cache entry to journal store
        SKPath? CachedPath = null);      // pre-built Skia path; reused every frame to avoid O(n) rebuild

    private readonly List<StrokeCache> _cachedStrokes = new();
    private readonly Stack<(StrokeCache Stroke, bool WasErased)> _undoHistory = new();
    private readonly Stack<(StrokeCache Stroke, bool WasErased)> _redoHistory = new();

    // Active stroke raw points (content-space).
    private List<Point>? _activeStroke;
    private Color _activeStrokeColor;
    private double _activeStrokeWidth;
    private bool _activeIsHighlight;
    private int _activeAnchorChapter;         // 1-based
    private int _activeAnchorLocalIndex = -1; // within-chapter
    private double _activeAnchorContentTop;

    private double _scrollOffsetY;
    private double _textColumnOffsetX;

    // ── Paragraph position provider (set by MainView) ────────────────────────

    /// <summary>
    /// Given a content-space Y, returns the chapter number, within-chapter paragraph index,
    /// and content-space top of the nearest realized paragraph.
    /// Used to anchor strokes to paragraphs at draw time.
    /// </summary>
    public Func<double, (int Chapter, int LocalIndex, double ContentTop)?>? FindParagraphAtContentY { get; set; }

    /// <summary>
    /// Given a (chapter, withinChapterIndex) pair, returns its current content-space top.
    /// Returns null if the chapter is not currently realized in the window.
    /// Used at render time to correct for virtualizing-panel drift.
    /// </summary>
    public Func<int, int, double?>? GetParagraphContentTop { get; set; }

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
        Redraw();
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
        Redraw();
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
        _activeAnchorChapter     = anchor?.Chapter    ?? 0;
        _activeAnchorLocalIndex  = anchor?.LocalIndex ?? -1;
        _activeAnchorContentTop  = anchor?.ContentTop ?? 0;

        _activeStroke = new List<Point> { contentPt };
        Redraw();
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
        var contentPt = ToContent(viewportPoint);
        // Skip near-duplicate points — reduces point count and mitigates render-
        // thread coalescing pressure. Threshold ~1.4 px in content space.
        var last = _activeStroke[_activeStroke.Count - 1];
        var dx = contentPt.X - last.X;
        var dy = contentPt.Y - last.Y;
        if (dx * dx + dy * dy < 2.0) return;
        _activeStroke.Add(contentPt);
        RedrawActiveStrokeLayer();
    }

    /// <summary>Finish the current stroke. No-op in eraser mode.</summary>
    public void EndStroke()
    {
        if (IsEraserMode) return;
        if (_activeStroke != null && _activeStroke.Count > 0)
        {
            var id = Guid.NewGuid().ToString();
            _redoHistory.Clear();
            if (_activeStroke.Count == 1)
            {
                var p = _activeStroke[0];
                var dotStroke = new StrokeCache(
                    p,
                    new Rect(p.X - 2, p.Y - 2, 4, 4),
                    _activeStrokeColor, _activeStrokeWidth, _activeIsHighlight, null,
                    _activeAnchorChapter, _activeAnchorLocalIndex, _activeAnchorContentTop, id);
                _cachedStrokes.Add(dotStroke);
                _undoHistory.Push((dotStroke, false));
                StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
                {
                    StrokeId             = id,
                    Points               = [],
                    Color                = _activeStrokeColor,
                    StrokeWidth          = _activeStrokeWidth,
                    IsHighlight          = _activeIsHighlight,
                    AnchorChapter        = _activeAnchorChapter,
                    AnchorParagraphIndex = _activeAnchorLocalIndex,
                    AnchorContentTop     = _activeAnchorContentTop
                });
            }
            else
            {
                var pts = _activeStroke.AsReadOnly();
                var multiStroke = new StrokeCache(
                    default,
                    ComputeBounds(_activeStroke),
                    _activeStrokeColor,
                    _activeStrokeWidth,
                    _activeIsHighlight,
                    pts,
                    _activeAnchorChapter,
                    _activeAnchorLocalIndex,
                    _activeAnchorContentTop,
                    id,
                    CachedPath: BuildSmoothPath(pts));
                _cachedStrokes.Add(multiStroke);
                _undoHistory.Push((multiStroke, false));
                StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
                {
                    StrokeId             = id,
                    Points               = pts,
                    Color                = _activeStrokeColor,
                    StrokeWidth          = _activeStrokeWidth,
                    IsHighlight          = _activeIsHighlight,
                    AnchorChapter        = _activeAnchorChapter,
                    AnchorParagraphIndex = _activeAnchorLocalIndex,
                    AnchorContentTop     = _activeAnchorContentTop
                });
            }
        }
        _activeStroke = null;
        _activeAnchorChapter    = 0;
        _activeAnchorLocalIndex = -1;
        _activeAnchorContentTop = 0;
        Redraw();
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
        _undoHistory.Clear();
        _redoHistory.Clear();
        if (state != null)
            _cachedStrokes.AddRange(state.Strokes);
        _activeStroke = null;
        Redraw();
    }

    /// <summary>Erase all strokes.</summary>
    public void ClearStrokes()
    {
        _cachedStrokes.Clear();
        _undoHistory.Clear();
        _redoHistory.Clear();
        _activeStroke = null;
        Redraw();
    }

    /// <summary>Load strokes from a persisted journal, replacing any existing strokes.</summary>
    public void LoadJournalStrokes(IReadOnlyList<JournalInkStroke> strokes)
    {
        _cachedStrokes.Clear();
        _undoHistory.Clear();
        _redoHistory.Clear();
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
                    stroke.AnchorChapter, stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id));
            }
            else
            {
                _cachedStrokes.Add(new StrokeCache(
                    default,
                    ComputeBounds(pts),
                    color, stroke.StrokeWidth, stroke.IsHighlight,
                    pts,
                    stroke.AnchorChapter, stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id,
                    CachedPath: BuildSmoothPath(pts)));
            }
        }
        Redraw();
    }

    /// <summary>
    /// Appends strokes for one chapter entering the scroll window.
    /// Does not clear existing strokes from other chapters.
    /// </summary>
    public void AppendChapterStrokes(IReadOnlyList<JournalInkStroke> strokes)
    {
        foreach (var stroke in strokes)
        {
            var pts = stroke.Points.Select(p => new Point(p.X, p.Y)).ToList();
            var color = Color.Parse(stroke.Color.Length > 0 ? stroke.Color : "#FF000000");
            if (pts.Count == 0) continue;

            if (pts.Count == 1)
            {
                var p = pts[0];
                _cachedStrokes.Add(new StrokeCache(
                    p, new Rect(p.X - 2, p.Y - 2, 4, 4),
                    color, stroke.StrokeWidth, stroke.IsHighlight, null,
                    stroke.AnchorChapter, stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id));
            }
            else
            {
                _cachedStrokes.Add(new StrokeCache(
                    default, ComputeBounds(pts),
                    color, stroke.StrokeWidth, stroke.IsHighlight, pts,
                    stroke.AnchorChapter, stroke.AnchorParagraphIndex, stroke.AnchorContentTop, stroke.Id,
                    CachedPath: BuildSmoothPath(pts)));
            }
        }
        Redraw();
    }

    /// <summary>
    /// Removes all strokes whose AnchorChapter matches the given chapter.
    /// Called when a chapter leaves the scroll window.
    /// </summary>
    public void RemoveChapterStrokes(int chapter)
    {
        var countBefore = _cachedStrokes.Count;
        _cachedStrokes.RemoveAll(s => s.AnchorChapter == chapter);
        if (_cachedStrokes.Count != countBefore)
            Redraw();
    }

    /// <summary>Reverses the most recent draw or erase action.</summary>
    public void UndoStroke()
    {
        if (_undoHistory.Count == 0) return;
        var (stroke, wasErased) = _undoHistory.Pop();
        _redoHistory.Push((stroke, wasErased));

        if (wasErased)
        {
            // Stroke was erased — restore it.
            _cachedStrokes.Add(stroke);
            Redraw();
            if (!string.IsNullOrEmpty(stroke.StrokeId))
            {
                var pts = stroke.Points ?? (IReadOnlyList<Point>)[];
                StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
                {
                    StrokeId             = stroke.StrokeId,
                    Points               = pts,
                    Color                = stroke.Color,
                    StrokeWidth          = stroke.StrokeWidth,
                    IsHighlight          = stroke.IsHighlight,
                    AnchorChapter        = stroke.AnchorChapter,
                    AnchorParagraphIndex = stroke.AnchorParagraphIndex,
                    AnchorContentTop     = stroke.AnchorContentTop
                });
            }
        }
        else
        {
            // Stroke was drawn — remove it.
            var idx = string.IsNullOrEmpty(stroke.StrokeId)
                ? _cachedStrokes.FindIndex(x => x == stroke)
                : _cachedStrokes.FindIndex(x => x.StrokeId == stroke.StrokeId);
            if (idx >= 0) _cachedStrokes.RemoveAt(idx);
            Redraw();
            if (!string.IsNullOrEmpty(stroke.StrokeId))
                StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(
                    [(stroke.StrokeId, stroke.AnchorChapter)]));
        }
    }

    /// <summary>Re-applies the most recently undone draw or erase action.</summary>
    public void RedoStroke()
    {
        if (_redoHistory.Count == 0) return;
        var (stroke, wasErased) = _redoHistory.Pop();
        _undoHistory.Push((stroke, wasErased));

        if (wasErased)
        {
            // Originally erased — re-erase it.
            var idx = string.IsNullOrEmpty(stroke.StrokeId)
                ? _cachedStrokes.FindIndex(x => x == stroke)
                : _cachedStrokes.FindIndex(x => x.StrokeId == stroke.StrokeId);
            if (idx >= 0) _cachedStrokes.RemoveAt(idx);
            Redraw();
            if (!string.IsNullOrEmpty(stroke.StrokeId))
                StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(
                    [(stroke.StrokeId, stroke.AnchorChapter)]));
        }
        else
        {
            // Originally drawn — re-add it.
            _cachedStrokes.Add(stroke);
            Redraw();
            if (!string.IsNullOrEmpty(stroke.StrokeId))
            {
                var pts = stroke.Points ?? (IReadOnlyList<Point>)[];
                StrokeCompleted?.Invoke(this, new InkStrokeEventArgs
                {
                    StrokeId             = stroke.StrokeId,
                    Points               = pts,
                    Color                = stroke.Color,
                    StrokeWidth          = stroke.StrokeWidth,
                    IsHighlight          = stroke.IsHighlight,
                    AnchorChapter        = stroke.AnchorChapter,
                    AnchorParagraphIndex = stroke.AnchorParagraphIndex,
                    AnchorContentTop     = stroke.AnchorContentTop
                });
            }
        }
    }

    // ── Pointer events (fired when MainView gives us pointer capture) ─────────

    private bool IsAcceptedPointerType(PointerType type) =>
        type == PointerType.Pen || (AllowMouseInput && type == PointerType.Mouse);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsAcceptedPointerType(e.Pointer.Type)) return;

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
        if (!IsAcceptedPointerType(e.Pointer.Type)) return;

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
        List<(string StrokeId, int Chapter)>? removedStrokes = null;

        for (int i = _cachedStrokes.Count - 1; i >= 0; i--)
        {
            var s = _cachedStrokes[i];
            var delta = GetDriftDelta(s.AnchorChapter, s.AnchorParagraphIndex, s.AnchorContentTop);
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
                        (removedStrokes ??= []).Add((s.StrokeId, s.AnchorChapter));
                    _undoHistory.Push((s, true));
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
                    (removedStrokes ??= []).Add((s.StrokeId, s.AnchorChapter));
                _undoHistory.Push((s, true));
                _cachedStrokes.RemoveAt(i);
            }
        }

        if (removedStrokes != null)
        {
            _redoHistory.Clear();
            Redraw();
            StrokeRemoved?.Invoke(this, new InkStrokeRemovedEventArgs(removedStrokes));
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a smooth Catmull-Rom spline (converted to cubic Béziers) through all
    /// input points. Called once per completed stroke; the result is cached in
    /// <see cref="StrokeCache.CachedPath"/> and reused on every render frame.
    /// </summary>
    private static SKPath BuildSmoothPath(IReadOnlyList<Point> pts)
    {
        var path = new SKPath();
        if (pts.Count < 2) return path;

        path.MoveTo((float)pts[0].X, (float)pts[0].Y);

        if (pts.Count == 2)
        {
            path.LineTo((float)pts[1].X, (float)pts[1].Y);
            return path;
        }

        // Catmull-Rom → cubic Bézier: each segment uses adjacent points as
        // Catmull-Rom tangent sources, producing C1-continuous smooth curves
        // that pass through every captured point. Recovers visual quality even
        // when pointer events were coalesced under render load.
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Count - 1, i + 2)];

            float cp1x = (float)(p1.X + (p2.X - p0.X) / 6.0);
            float cp1y = (float)(p1.Y + (p2.Y - p0.Y) / 6.0);
            float cp2x = (float)(p2.X - (p3.X - p1.X) / 6.0);
            float cp2y = (float)(p2.Y - (p3.Y - p1.Y) / 6.0);

            path.CubicTo(cp1x, cp1y, cp2x, cp2y, (float)p2.X, (float)p2.Y);
        }

        return path;
    }

    /// <summary>
    /// Compute drift delta for a stroke's anchor paragraph. Returns 0 if no
    /// anchor is available or the paragraph position can't be resolved.
    /// </summary>
    private double GetDriftDelta(int anchorChapter, int anchorLocalIndex, double anchorContentTop)
    {
        if (anchorChapter <= 0 || anchorLocalIndex < 0 || GetParagraphContentTop == null) return 0;
        var currentTop = GetParagraphContentTop(anchorChapter, anchorLocalIndex);
        return currentTop.HasValue ? currentTop.Value - anchorContentTop : 0;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var src  = DataSource ?? this;
        var mode = DrawMode;

        if (src._cachedStrokes.Count == 0 && src._activeStroke == null) return;

        using var clip = context.PushClip(new Rect(Bounds.Size));

        double viewTop    = src._scrollOffsetY - 2000;
        double viewBottom = src._scrollOffsetY + Bounds.Height + 2000;

        List<(StrokeCache Stroke, double DriftDelta)>? highlightStrokes = null;
        List<(StrokeCache Stroke, double DriftDelta)>? penStrokes = null;

        foreach (var s in src._cachedStrokes)
        {
            var delta = src.GetDriftDelta(s.AnchorChapter, s.AnchorParagraphIndex, s.AnchorContentTop);
            var top   = s.ContentBounds.Top    + delta;
            var bot   = s.ContentBounds.Bottom + delta;
            if (bot < viewTop || top > viewBottom) continue;

            if (s.IsHighlight)
            {
                if (mode != InkDrawMode.PenOnly)
                    (highlightStrokes ??= new()).Add((s, delta));
            }
            else
            {
                if (mode != InkDrawMode.HighlightOnly)
                    (penStrokes ??= new()).Add((s, delta));
            }
        }

        StrokeCache? activeHighlight = null;
        double activeHighlightDelta  = 0;
        StrokeCache? activePen       = null;
        double activePenDelta        = 0;

        if (src._activeStroke != null && src._activeStroke.Count > 0)
        {
            var pts = src._activeStroke.AsReadOnly();
            if (src._activeIsHighlight)
            {
                if (mode != InkDrawMode.PenOnly)
                {
                    activeHighlight      = new StrokeCache(default, default, src._activeStrokeColor, src._activeStrokeWidth, true, pts, src._activeAnchorChapter, src._activeAnchorLocalIndex, src._activeAnchorContentTop);
                    activeHighlightDelta = src.GetDriftDelta(src._activeAnchorChapter, src._activeAnchorLocalIndex, src._activeAnchorContentTop);
                }
            }
            else
            {
                if (mode != InkDrawMode.HighlightOnly)
                {
                    activePen      = new StrokeCache(default, default, src._activeStrokeColor, src._activeStrokeWidth, false, pts, src._activeAnchorChapter, src._activeAnchorLocalIndex, src._activeAnchorContentTop);
                    activePenDelta = src.GetDriftDelta(src._activeAnchorChapter, src._activeAnchorLocalIndex, src._activeAnchorContentTop);
                }
            }
        }

        if (highlightStrokes != null || penStrokes != null || activeHighlight.HasValue || activePen.HasValue)
            context.Custom(new SkiaInkDrawOperation(
                new Rect(Bounds.Size),
                highlightStrokes, penStrokes,
                activeHighlight, activeHighlightDelta,
                activePen, activePenDelta,
                src._scrollOffsetY, src._textColumnOffsetX));
    }

    // ── Skia ink draw operation ───────────────────────────────────────────────────

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
            };

            // Highlights use Multiply so they darken text rather than covering it.
            paint.BlendMode = SKBlendMode.Multiply;
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

            // Pen strokes use SrcOver; they live on a canvas below the text so text reads over them.
            paint.BlendMode = SKBlendMode.SrcOver;
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

            // Use the pre-built cached path (all completed strokes); fall back to
            // on-the-fly LineTo only for the active (in-progress) stroke.
            if (stroke.CachedPath != null)
            {
                canvas.DrawPath(stroke.CachedPath, paint);
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
