using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MyBibleApp.Controls;
using MyBibleApp.PanZoom;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public class ChapterNavigationEventArgs : EventArgs
{
    public string BookCode { get; }
    public int Chapter { get; }

    public ChapterNavigationEventArgs(string bookCode, int chapter)
    {
        BookCode = bookCode;
        Chapter = chapter;
    }
}

public partial class BibleReadingView : UserControl
{
    // Raised when the user taps the close button.
    public event EventHandler? CloseRequested;

    // Raised when the user requests navigation to a specific chapter.
    public event EventHandler<ChapterNavigationEventArgs>? ChapterNavigationRequested;

    private TextBlock? _progressSummary;
    private Grid? _booksGrid;
    private PanZoomView? _panZoom;

    public BibleReadingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _progressSummary = this.FindControl<TextBlock>("ProgressSummary");
        _booksGrid = this.FindControl<Grid>("BooksGrid");
        _panZoom = this.FindControl<PanZoomView>("PanZoom");

        _panZoom!.LayoutUpdated += (_, _) => UpdateBooksGridWidth();

        UpdateProgressSummary();

        // Refresh the summary when LastUpdated is set by the async load.
        if (DataContext is BibleReadingViewModel vm)
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BibleReadingViewModel.LastUpdated))
                    UpdateProgressSummary();
            };

        // Listen for the bubbling routed event from any ChapterGridControl
        AddHandler(ChapterGridControl.ChapterCellClickedEvent, OnChapterCellClicked);
    }

    // ── Progress label ────────────────────────────────────────────────────────

    private void UpdateProgressSummary()
    {
        if (_progressSummary == null || DataContext is not BibleReadingViewModel vm) return;

        var allBooks   = vm.OtBooks.Concat(vm.NtBooks).ToList();
        var totalChaps = allBooks.Sum(b => b.Chapters.Count);
        var readChaps  = allBooks.Sum(b => b.Chapters.Count(c => c.IsRead));

        var summary = $"{readChaps} of {totalChaps} chapters read";
        if (vm.LastUpdated.HasValue)
            summary += $" · Updated {vm.LastUpdated.Value:MMM d, yyyy}";

        _progressSummary.Text = summary;
    }

    // ── Chapter cell click → show flyout ──────────────────────────────────────

    private void OnChapterCellClicked(object? sender, ChapterCellClickedEventArgs e)
    {
        var cell = e.Cell;
        var grid = e.SourceGrid;

        // Find the book name for display
        var bookName = cell.BookCode;
        if (DataContext is BibleReadingViewModel vm)
        {
            var book = vm.OtBooks.Concat(vm.NtBooks)
                .FirstOrDefault(b => b.Code == cell.BookCode);
            if (book != null) bookName = book.Name;
        }

        var goToButton = new Button
        {
            Content = $"Go to {bookName} {cell.Number}",
            Classes = { "flyout-item" }
        };

        var markReadLabel = cell.IsRead ? "Mark as unread" : "Mark as read";
        var markReadButton = new Button
        {
            Content = markReadLabel,
            Classes = { "flyout-item" }
        };

        var panel = new StackPanel { MinWidth = 160 };
        panel.Children.Add(goToButton);
        panel.Children.Add(markReadButton);

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            HorizontalOffset = e.CellRect.X,
            VerticalOffset = e.CellRect.Bottom - grid.Bounds.Height,
            Content = panel
        };

        goToButton.Click += (_, _) =>
        {
            flyout.Hide();
            ChapterNavigationRequested?.Invoke(this,
                new ChapterNavigationEventArgs(cell.BookCode, cell.Number));
        };

        markReadButton.Click += (_, _) =>
        {
            cell.IsRead = !cell.IsRead;
            flyout.Hide();
            if (DataContext is BibleReadingViewModel vmInner)
                _ = vmInner.SaveAsync();
            UpdateProgressSummary();
        };

        flyout.ShowAt(grid);
    }

    // ── Debug refresh ─────────────────────────────────────────────────────────

    private void OnDebugRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BibleReadingViewModel vm)
            _ = vm.RefreshSyncDebugInfoAsync();
    }

    private void OnDebugSyncNowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BibleReadingViewModel vm)
            vm.AppVM.ForceSync();
    }

    // ── Responsive width ──────────────────────────────────────────────────────

    private const double MinBooksGridWidth = 600;

    private void UpdateBooksGridWidth()
    {
        if (_booksGrid == null || _panZoom == null) return;

        // Bounds (PanZoomView's own outer size, from its parent) rather than the inner
        // ScrollViewer's Viewport: Viewport shrinks/grows depending on whether the vertical
        // scrollbar is currently shown, and scrollbar visibility itself depends on zoom — using
        // Viewport here made "natural" (unscaled) width a moving target that could change out
        // from under PanZoomView's last-computed extent right as a zoom-out crossed the
        // threshold where the scrollbar disappears, permanently desyncing the two and leaving
        // the ScrollViewer believing there was nothing left to scroll.
        var availableWidth = _panZoom.Bounds.Width;
        if (availableWidth <= 0) return;
        var hMargin = _booksGrid.Margin.Left + _booksGrid.Margin.Right;
        var newWidth = Math.Max(MinBooksGridWidth, availableWidth - hMargin);
        if (Math.Abs(newWidth - _booksGrid.Width) < 0.5) return;
        _booksGrid.Width = newWidth;
    }

    // ── Scroll to current passage ─────────────────────────────────────────────

    private void OnScrollToCurrentClick(object? sender, RoutedEventArgs e)
    {
        var scrollViewer = _panZoom?.InnerScrollViewer;
        if (scrollViewer == null) return;

        foreach (var grid in this.GetVisualDescendants().OfType<ChapterGridControl>())
        {
            var cellRect = grid.GetCurrentChapterCellRect();
            if (cellRect == null) continue;

            var cellCenter = new Point(
                cellRect.Value.X + cellRect.Value.Width / 2,
                cellRect.Value.Y + cellRect.Value.Height / 2);

            // Translate cell center to ScrollViewer viewport coordinates, then add
            // current scroll offset to get content-space coordinates.
            var pt = grid.TranslatePoint(cellCenter, scrollViewer);
            if (pt == null) return;

            var contentX = pt.Value.X + scrollViewer.Offset.X;
            var contentY = pt.Value.Y + scrollViewer.Offset.Y;

            scrollViewer.Offset = new Vector(
                Math.Max(0, contentX - scrollViewer.Viewport.Width / 2),
                Math.Max(0, contentY - scrollViewer.Viewport.Height / 2));
            return;
        }
    }

    // ── Close ─────────────────────────────────────────────────────────────────

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
