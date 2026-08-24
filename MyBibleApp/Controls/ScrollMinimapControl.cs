using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MyBibleApp.Controls;

/// <summary>
/// Debug-only vertical strip showing the whole book proportionally: which chapters are
/// currently realized in the scroll window vs. virtual/unloaded, plus the current viewport's
/// position within the whole. Purely visual (IsHitTestVisible should be set false by the
/// caller) — a dev aid for the windowed-scrolling/chapter-load-jitter class of bugs, not
/// user-facing. Custom-drawn (OnRender), matching the pattern already used by
/// <see cref="InkOverlayCanvas"/> and <see cref="ParagraphInkCanvas"/> in this codebase,
/// rather than one child visual per chapter.
///
/// The chapter strip and viewport band are both drawn directly on every InvalidateVisual — see
/// the comment in Render for why a RenderTargetBitmap cache was tried and reverted.
/// </summary>
public class ScrollMinimapControl : Control
{
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
    private static readonly IBrush LoadedBrush = new SolidColorBrush(Color.FromArgb(160, 80, 160, 255));
    private static readonly IBrush UnloadedBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
    private static readonly IBrush ViewportBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
    private static readonly IPen ViewportPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1);
    private static readonly IBrush EnteredFlashBrush = new SolidColorBrush(Color.FromArgb(220, 90, 220, 120));
    private static readonly IBrush ExitedFlashBrush = new SolidColorBrush(Color.FromArgb(220, 220, 90, 90));

    private const double FlashDurationMs = 400;

    private IReadOnlyList<double> _virtualHeights = [];
    private int _windowStart;
    private int _windowEnd;

    // Viewport indicator, expressed as an inclusive 1-based CHAPTER range rather than as raw
    // scroll pixels. Pixels were wrong: the chapter strip is proportioned against
    // sum(_virtualHeights), while ScrollViewer.Extent.Height is estimate-based spacers plus
    // real measured loaded content — two different totals, so a pixel fraction of the extent
    // did not land where the same fraction of the strip is drawn. Chapter indices are the one
    // coordinate both sides genuinely share.
    private int _viewportTopChapter;
    private int _viewportBottomChapter;

    // chapter (1-based) -> (entered?, flash start tick)
    private readonly Dictionary<int, (bool Entered, long StartTicks)> _flashes = new();
    private DispatcherTimer? _flashTimer;

    // Diagnostics — `adb logcat | grep MBA_MINIMAP`. Logs the band's geometry against the
    // loaded-window range whenever it moves, so a future mismatch between the band and the
    // blue region can be checked against numbers rather than inferred from a screenshot.
    private const string MinimapLogTag = "MBA_MINIMAP";
    private int _lastLoggedBandTop = -1;
    private int _lastLoggedBandBottom = -1;

    /// <summary>
    /// Supplies per-chapter heights for the strip. Call when they change (window mutations).
    ///
    /// Deliberately does NOT take the window range. It used to, via UpdateSpacers, and that was
    /// wrong twice over: UpdateSpacers runs BEFORE _windowStart++ in TrimWindowTop and before
    /// _windowEnd++ in ExtendWindowDown (so the range arrived stale), and in both
    /// ExtendWindowUp/Down the UpdateSpacers call sits inside an
    /// `if (_windowStart/_windowEnd &lt; _virtualHeights.Length)` guard (so on those paths the
    /// minimap was never told at all). Missed and stale notifications accumulated, drifting the
    /// blue loaded-region several chapters away from reality while the viewport band — pushed
    /// fresh on every scroll tick — stayed correct. That is why the band appeared outside the
    /// blue. The range now arrives with the viewport instead; see SetViewport.
    /// </summary>
    public void SetChapterHeights(IReadOnlyList<double> virtualHeights)
    {
        _virtualHeights = virtualHeights;
        InvalidateVisual();
    }

    /// <summary>
    /// Repositions the viewport-indicator band, given the inclusive 1-based range of chapters
    /// currently visible. Call on every scroll tick. Also carries the loaded-window range
    /// (windowStart/windowEnd) rather than relying on SetChapterHeights' caller to keep it in
    /// sync — see SetChapterHeights for why that used to drift.
    /// </summary>
    public void SetViewport(int topChapter, int bottomChapter, int windowStart, int windowEnd)
    {
        var windowChanged = _windowStart != windowStart || _windowEnd != windowEnd;
        var viewportChanged = _viewportTopChapter != topChapter || _viewportBottomChapter != bottomChapter;
        if (!windowChanged && !viewportChanged)
            return;

        _viewportTopChapter = topChapter;
        _viewportBottomChapter = bottomChapter;
        _windowStart = windowStart;
        _windowEnd = windowEnd;

        InvalidateVisual();
    }

    /// <summary>Briefly pulses a chapter's segment: green for entered-window, red for exited.</summary>
    public void FlashChapter(int chapter, bool entered)
    {
        _flashes[chapter] = (entered, Environment.TickCount64);
        InvalidateVisual();

        _flashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 20) };
        if (!_flashTimer.IsEnabled)
        {
            _flashTimer.Tick += OnFlashTimerTick;
            _flashTimer.Start();
        }
    }

    private void OnFlashTimerTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        List<int>? expired = null;
        foreach (var (chapter, state) in _flashes)
        {
            if (now - state.StartTicks >= FlashDurationMs)
                (expired ??= []).Add(chapter);
        }

        if (expired != null)
            foreach (var chapter in expired)
                _flashes.Remove(chapter);

        InvalidateVisual();

        if (_flashes.Count == 0)
            _flashTimer!.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // Drawn directly, NOT via a cached RenderTargetBitmap. The cache was a real
        // optimisation — it stopped 150 FillRectangle calls happening on every scroll tick —
        // but on a high-DPI device the blitted result landed at a visibly different scale from
        // the band drawn beside it: diagnostics proved the two agreed numerically (identical
        // total, n, window, and a band strictly inside the blue range) while the screen showed
        // them far apart, which can only be the DrawImage/DPI path. Not worth chasing
        // RenderTargetBitmap's DPI and source-rect semantics for a debug overlay: 150 rect fills
        // is trivial for Skia, and the reason the per-tick cost mattered before was that window
        // mutation was saturating the UI thread — which the coast-deferral fix has since
        // addressed. If this ever does show up on the FPS overlay, cache it again by drawing
        // into a fresh bitmap per redraw rather than reusing one.
        DrawChapterStrip(context, bounds);
        DrawViewportBand(context, bounds);
    }

    /// <summary>
    /// Draws the viewport band by accumulating the SAME per-chapter heights the strip is drawn
    /// from, so the band and the segments it sits over are always in one coordinate space.
    /// </summary>
    private void DrawViewportBand(DrawingContext context, Rect bounds)
    {
        if (_viewportTopChapter <= 0 || _viewportBottomChapter <= 0 || _virtualHeights.Count == 0)
            return;

        var totalHeight = 0.0;
        foreach (var h in _virtualHeights)
            totalHeight += h;
        if (totalHeight <= 0)
            return;

        // Chapters are 1-based; _virtualHeights is 0-based.
        var topIdx = Math.Clamp(_viewportTopChapter - 1, 0, _virtualHeights.Count - 1);
        var bottomIdx = Math.Clamp(_viewportBottomChapter - 1, topIdx, _virtualHeights.Count - 1);

        var yStart = 0.0;
        for (var i = 0; i < topIdx; i++)
            yStart += _virtualHeights[i];

        var span = 0.0;
        for (var i = topIdx; i <= bottomIdx; i++)
            span += _virtualHeights[i];

        var top = yStart / totalHeight * bounds.Height;
        var height = Math.Max(2, span / totalHeight * bounds.Height);

        // Diagnostic: both the band and the strip index the same _virtualHeights with the same
        // normalisation, and GetVisibleChapterRange clamps its result to the loaded window, so
        // the band should never visually fall outside the blue region. Kept after the cache
        // removal (which fixed a real DPI/DrawImage mismatch) as a cheap tripwire in case a
        // future change reintroduces a coordinate-space split between the two.
        if (_lastLoggedBandTop != topIdx || _lastLoggedBandBottom != bottomIdx)
        {
            _lastLoggedBandTop = topIdx;
            _lastLoggedBandBottom = bottomIdx;
            Console.WriteLine($"[{MinimapLogTag}] band ch={_viewportTopChapter}..{_viewportBottomChapter} " +
                $"idx={topIdx}..{bottomIdx} win={_windowStart}..{_windowEnd} " +
                $"y={top:F1}+{height:F1} of {bounds.Height:F1} total={totalHeight:F0} n={_virtualHeights.Count}");
        }

        var rect = new Rect(0, top, bounds.Width, height);
        context.FillRectangle(ViewportBrush, rect);
        context.DrawRectangle(ViewportPen, rect);
    }

    private void DrawChapterStrip(DrawingContext context, Rect bounds)
    {
        var size = bounds.Size;
        context.FillRectangle(TrackBrush, new Rect(size));

        var totalHeight = 0.0;
        foreach (var h in _virtualHeights)
            totalHeight += h;

        if (totalHeight <= 0 || _virtualHeights.Count == 0)
            return;

        var now = Environment.TickCount64;
        var y = 0.0;
        for (var i = 0; i < _virtualHeights.Count; i++)
        {
            var segHeight = _virtualHeights[i] / totalHeight * size.Height;
            var chapter = i + 1; // 1-based

            IBrush brush = i >= _windowStart && i < _windowEnd ? LoadedBrush : UnloadedBrush;
            if (_flashes.TryGetValue(chapter, out var flash))
            {
                var age = now - flash.StartTicks;
                if (age < FlashDurationMs)
                {
                    var fade = 1.0 - age / FlashDurationMs;
                    var baseBrush = (SolidColorBrush)(flash.Entered ? EnteredFlashBrush : ExitedFlashBrush);
                    brush = new SolidColorBrush(baseBrush.Color) { Opacity = fade };
                }
            }

            // Leave a 1px seam between segments so chapter boundaries stay legible at
            // small sizes instead of blurring into one solid block.
            if (segHeight > 1)
                context.FillRectangle(brush, new Rect(0, y, size.Width, segHeight - 1));

            y += segHeight;
        }
    }
}
