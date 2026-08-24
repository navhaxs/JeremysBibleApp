using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
/// The chapter strip (track + per-chapter segments + flashes) is cached to a
/// <see cref="RenderTargetBitmap"/> and only re-rendered when chapters/window/flash state
/// actually change. SetViewport — called on every scroll tick — only repositions a thin
/// band drawn fresh each frame; it used to also redraw the whole chapter loop (up to ~150
/// FillRectangle calls for a book like Psalms) on every single tick, which was measurable
/// per-frame cost during active touch scrolling.
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

    private double _viewportOffsetY;
    private double _viewportHeight;
    private double _extentHeight;

    // chapter (1-based) -> (entered?, flash start tick)
    private readonly Dictionary<int, (bool Entered, long StartTicks)> _flashes = new();
    private DispatcherTimer? _flashTimer;

    private RenderTargetBitmap? _chapterStripCache;
    private Size _chapterStripCacheSize;
    private double _chapterStripCacheScaling;
    private bool _chapterStripDirty = true;

    /// <summary>Redraws the proportional chapter strip. Call whenever the window or virtual heights change.</summary>
    public void SetChapters(IReadOnlyList<double> virtualHeights, int windowStart, int windowEnd)
    {
        _virtualHeights = virtualHeights;
        _windowStart = windowStart;
        _windowEnd = windowEnd;
        _chapterStripDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Repositions the viewport-indicator band. Call on every scroll tick — cheap: does not
    /// touch the cached chapter strip, only the thin band drawn fresh on top of it each frame.
    /// </summary>
    public void SetViewport(double offsetY, double viewportHeight, double extentHeight)
    {
        _viewportOffsetY = offsetY;
        _viewportHeight = viewportHeight;
        _extentHeight = extentHeight;
        InvalidateVisual();
    }

    /// <summary>Briefly pulses a chapter's segment: green for entered-window, red for exited.</summary>
    public void FlashChapter(int chapter, bool entered)
    {
        _flashes[chapter] = (entered, Environment.TickCount64);
        _chapterStripDirty = true;
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

        _chapterStripDirty = true;
        InvalidateVisual();

        if (_flashes.Count == 0)
            _flashTimer!.Stop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _chapterStripCache?.Dispose();
        _chapterStripCache = null;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // Only reallocate the bitmap when its pixel size actually needs to change (control
        // resized, or the window moved to a different-DPI screen). Content-only changes
        // (SetChapters, flash animation ticks) reuse the same bitmap via CreateDrawingContext,
        // which just clears and redraws it — no repeated GPU/CPU surface allocation.
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (_chapterStripCache == null || _chapterStripCacheSize != bounds.Size || _chapterStripCacheScaling != scaling)
        {
            _chapterStripCache?.Dispose();
            var pixelSize = new PixelSize(
                Math.Max(1, (int)(bounds.Width * scaling)),
                Math.Max(1, (int)(bounds.Height * scaling)));
            _chapterStripCache = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
            _chapterStripCacheSize = bounds.Size;
            _chapterStripCacheScaling = scaling;
            _chapterStripDirty = true;
        }

        if (_chapterStripDirty)
        {
            RenderChapterStripCache(bounds.Size);
            _chapterStripDirty = false;
        }

        if (_chapterStripCache != null)
            context.DrawImage(_chapterStripCache, new Rect(_chapterStripCache.Size), new Rect(bounds.Size));

        if (_extentHeight > 0 && _viewportHeight > 0)
        {
            var top = _viewportOffsetY / _extentHeight * bounds.Height;
            var height = Math.Max(2, _viewportHeight / _extentHeight * bounds.Height);
            var rect = new Rect(0, top, bounds.Width, height);
            context.FillRectangle(ViewportBrush, rect);
            context.DrawRectangle(ViewportPen, rect);
        }
    }

    private void RenderChapterStripCache(Size size)
    {
        using var context = _chapterStripCache!.CreateDrawingContext();
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
