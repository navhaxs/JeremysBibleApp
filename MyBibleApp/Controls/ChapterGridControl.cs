using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Controls;

/// <summary>
/// Custom-drawn grid of chapter cells for a single book.
/// Replaces hundreds of Button controls with a single Render pass.
/// </summary>
public class ChapterGridControl : Control
{
    private const double CellWidth = 26;
    private const double CellHeight = 22;
    private const double BorderThickness = 0.5;
    private const int ColumnsPerRow = 10;

    private const double TouchTapThreshold = 24;

    // Static — never change, no theme dependency
    private static readonly SolidColorBrush HoverBrush = new(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush PressedBrush = new(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);

    private int _hoverIndex = -1;
    private int _pressedIndex = -1;
    private Point _pressedPosition;
    private IReadOnlyList<BibleReadingChapterCell>? _chapters;

    // Theme-dependent cached resources — rebuilt in RebuildRenderResources()
    private Avalonia.Styling.ThemeVariant? _cachedThemeVariant;
    private IBrush? _cachedAccentBrush;
    private IBrush? _cachedAccentBorderBrush;
    private IBrush? _cachedCellBorderBrush;
    private Pen? _cachedBorderPen;
    private Pen? _cachedCurrentBorderPen;
    private SolidColorBrush? _cachedUnreadTextBrush;

    // BookCellColor cached brush — rebuilt when BookCellColorProperty changes
    private SolidColorBrush? _cachedCellBrush;

    // FormattedText caches — lazy-built, nulled on theme/chapters change
    private FormattedText?[]? _cachedReadTexts;
    private FormattedText?[]? _cachedUnreadTexts;
    private int _cachedTextCount;

    public static readonly StyledProperty<IReadOnlyList<BibleReadingChapterCell>?> ChaptersProperty =
        AvaloniaProperty.Register<ChapterGridControl, IReadOnlyList<BibleReadingChapterCell>?>(nameof(Chapters));

    public IReadOnlyList<BibleReadingChapterCell>? Chapters
    {
        get => GetValue(ChaptersProperty);
        set => SetValue(ChaptersProperty, value);
    }

    public static readonly StyledProperty<Color> BookCellColorProperty =
        AvaloniaProperty.Register<ChapterGridControl, Color>(nameof(BookCellColor));

    public Color BookCellColor
    {
        get => GetValue(BookCellColorProperty);
        set => SetValue(BookCellColorProperty, value);
    }

    /// <summary>Raised when a chapter cell is tapped. The sender provides the clicked cell.</summary>
    public event EventHandler<BibleReadingChapterCell>? CellClicked;

    /// <summary>Bubbling routed event so parent views can handle cell taps without wiring individual instances.</summary>
    public static readonly RoutedEvent<ChapterCellClickedEventArgs> ChapterCellClickedEvent =
        RoutedEvent.Register<ChapterGridControl, ChapterCellClickedEventArgs>(
            nameof(ChapterCellClicked), RoutingStrategies.Bubble);

    public event EventHandler<ChapterCellClickedEventArgs> ChapterCellClicked
    {
        add => AddHandler(ChapterCellClickedEvent, value);
        remove => RemoveHandler(ChapterCellClickedEvent, value);
    }

    static ChapterGridControl()
    {
        AffectsRender<ChapterGridControl>(ChaptersProperty);
        AffectsMeasure<ChapterGridControl>(ChaptersProperty);
        AffectsRender<ChapterGridControl>(BookCellColorProperty);
    }

    public ChapterGridControl()
    {
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RebuildRenderResources();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChaptersProperty)
        {
            UnsubscribeAll();
            _chapters = Chapters;
            SubscribeAll();
            _cachedReadTexts = null;
            _cachedUnreadTexts = null;
            _cachedTextCount = 0;
            InvalidateMeasure();
            InvalidateVisual();
        }

        if (change.Property == BookCellColorProperty)
        {
            _cachedCellBrush = new SolidColorBrush(BookCellColor);
            InvalidateVisual();
        }
    }

    private void RebuildRenderResources()
    {
        _cachedThemeVariant = ActualThemeVariant;
        _cachedAccentBrush = GetResourceBrush("ThemeAccentColor") ?? Brushes.DodgerBlue;
        _cachedAccentBorderBrush = GetResourceBrush("ThemeAccentBrush") ?? _cachedAccentBrush;
        _cachedCellBorderBrush = GetResourceBrush("SystemControlForegroundBaseMediumLowBrush") ?? Brushes.Gray;
        _cachedBorderPen = new Pen(_cachedCellBorderBrush, BorderThickness);
        _cachedCurrentBorderPen = new Pen(_cachedAccentBorderBrush, 2);
        var isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        _cachedUnreadTextBrush = isDark
            ? new SolidColorBrush(Color.FromRgb(200, 200, 200))
            : new SolidColorBrush(Color.FromRgb(60, 60, 60));
        // Unread texts embed the foreground brush — must lazy-rebuild with new color
        if (_cachedUnreadTexts != null)
            Array.Clear(_cachedUnreadTexts, 0, _cachedUnreadTexts.Length);
    }

    private readonly List<PropertyChangedEventHandler> _handlers = [];

    private void SubscribeAll()
    {
        if (_chapters == null) return;

        foreach (var cell in _chapters)
        {
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName is nameof(BibleReadingChapterCell.IsRead)
                    or nameof(BibleReadingChapterCell.IsCurrentChapter))
                {
                    InvalidateVisual();
                }
            };
            cell.PropertyChanged += handler;
            _handlers.Add(handler);
        }
    }

    private void UnsubscribeAll()
    {
        if (_chapters != null && _handlers.Count == _chapters.Count)
        {
            for (var i = 0; i < _chapters.Count; i++)
                _chapters[i].PropertyChanged -= _handlers[i];
        }
        _handlers.Clear();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = _chapters?.Count ?? 0;
        if (count == 0) return default;

        var rows = (int)Math.Ceiling((double)count / ColumnsPerRow);
        var cols = Math.Min(count, ColumnsPerRow);
        return new Size(cols * CellWidth, rows * CellHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Draw transparent background so the entire control area is hit-testable (needed for touch)
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        if (_chapters == null || _chapters.Count == 0) return;

        // Rebuild cached resources on theme change or first render
        if (_cachedThemeVariant != ActualThemeVariant || _cachedBorderPen == null)
            RebuildRenderResources();

        var borderPen = _cachedBorderPen!;
        var currentBorderPen = _cachedCurrentBorderPen!;
        IBrush normalForeground = _cachedUnreadTextBrush!;
        var cellBrush = (IBrush?)_cachedCellBrush ?? _cachedAccentBrush ?? Brushes.DodgerBlue;

        EnsureTextCacheSize(_chapters.Count);

        for (var i = 0; i < _chapters.Count; i++)
        {
            var cell = _chapters[i];
            var col = i % ColumnsPerRow;
            var row = i / ColumnsPerRow;
            var rect = new Rect(col * CellWidth, row * CellHeight, CellWidth, CellHeight);

            // Background
            if (cell.IsRead)
                context.DrawRectangle(cellBrush, null, rect);
            else if (i == _pressedIndex)
                context.DrawRectangle(PressedBrush, null, rect);
            else if (i == _hoverIndex)
                context.DrawRectangle(HoverBrush, null, rect);

            // Border
            if (cell.IsCurrentChapter)
            {
                var inset = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
                context.DrawRectangle(null, currentBorderPen, inset);
            }
            else
            {
                context.DrawRectangle(null, borderPen, rect);
            }

            // Text — lazy-built once per cell per display state, reused every frame
            var text = cell.IsRead
                ? (_cachedReadTexts![i] ??= CreateText(_chapters[i].Number, Brushes.White))
                : (_cachedUnreadTexts![i] ??= CreateText(_chapters[i].Number, normalForeground));

            var textX = rect.X + (rect.Width - text.Width) / 2;
            var textY = rect.Y + (rect.Height - text.Height) / 2;
            context.DrawText(text, new Point(textX, textY));
        }
    }

    private void EnsureTextCacheSize(int count)
    {
        if (_cachedTextCount == count) return;
        _cachedReadTexts = new FormattedText?[count];
        _cachedUnreadTexts = new FormattedText?[count];
        _cachedTextCount = count;
    }

    private static FormattedText CreateText(int number, IBrush foreground) =>
        new(number.ToString(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, DefaultTypeface, 9, foreground);

    private IBrush? GetResourceBrush(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush)
            return brush;
        // Some resources are Color, not Brush
        if (resource is Color color)
            return new SolidColorBrush(color);
        return null;
    }

    // ── Hit testing ──────────────────────────────────────────────────────────

    private int HitTestCell(Point position)
    {
        if (_chapters == null || _chapters.Count == 0) return -1;

        var col = (int)(position.X / CellWidth);
        var row = (int)(position.Y / CellHeight);

        if (col < 0 || col >= ColumnsPerRow || row < 0) return -1;

        var index = row * ColumnsPerRow + col;
        return index >= 0 && index < _chapters.Count ? index : -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var index = HitTestCell(e.GetPosition(this));
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        var needsRedraw = false;
        if (_hoverIndex != -1) { _hoverIndex = -1; needsRedraw = true; }
        if (_pressedIndex != -1) { _pressedIndex = -1; needsRedraw = true; }
        if (needsRedraw) InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        var index = HitTestCell(e.GetPosition(this));
        if (index >= 0)
        {
            _pressedIndex = index;
            _pressedPosition = e.GetPosition(this);
            InvalidateVisual();
            // Do NOT set e.Handled — let the event bubble so ScrollViewer can pan on touch
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_pressedIndex >= 0 && _chapters != null)
        {
            // Only fire click if the pointer hasn't moved significantly (not a pan gesture)
            var releasePos = e.GetPosition(this);
            var delta = releasePos - _pressedPosition;
            if (Math.Abs(delta.X) < TouchTapThreshold && Math.Abs(delta.Y) < TouchTapThreshold)
            {
                var releaseIndex = HitTestCell(releasePos);
                if (releaseIndex == _pressedIndex && releaseIndex < _chapters.Count)
                {
                    var cell = _chapters[releaseIndex];
                    var col = releaseIndex % ColumnsPerRow;
                    var row = releaseIndex / ColumnsPerRow;
                    var cellRect = new Rect(col * CellWidth, row * CellHeight, CellWidth, CellHeight);
                    CellClicked?.Invoke(this, cell);
                    RaiseEvent(new ChapterCellClickedEventArgs(cell, this, cellRect));
                }
            }
        }

        _pressedIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressedIndex != -1)
        {
            _pressedIndex = -1;
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeAll();
        base.OnDetachedFromVisualTree(e);
    }
}

public class ChapterCellClickedEventArgs : RoutedEventArgs
{
    public BibleReadingChapterCell Cell { get; }
    public ChapterGridControl SourceGrid { get; }
    /// <summary>The cell's bounding rectangle relative to the SourceGrid control.</summary>
    public Rect CellRect { get; }

    public ChapterCellClickedEventArgs(BibleReadingChapterCell cell, ChapterGridControl sourceGrid, Rect cellRect)
        : base(ChapterGridControl.ChapterCellClickedEvent)
    {
        Cell = cell;
        SourceGrid = sourceGrid;
        CellRect = cellRect;
    }
}
