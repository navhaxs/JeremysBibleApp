using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyBibleApp.Controls;
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

    public BibleReadingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _progressSummary = this.FindControl<TextBlock>("ProgressSummary");
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

    // ── Close ─────────────────────────────────────────────────────────────────

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
