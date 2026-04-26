using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class BibleReadingView : UserControl
{
    // Raised when the user taps the close button.
    public event EventHandler? CloseRequested;

    // ── Free-pan state ────────────────────────────────────────────────────────
    private ScrollViewer? _panScrollViewer;
    private TextBlock?    _progressSummary;
    private bool          _isPanning;
    private Point         _panStartPointer;
    private Vector        _panStartOffset;

    public BibleReadingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _panScrollViewer = this.FindControl<ScrollViewer>("PanScrollViewer");
        _progressSummary = this.FindControl<TextBlock>("ProgressSummary");
        UpdateProgressSummary();
    }

    // ── Progress label ────────────────────────────────────────────────────────

    private void UpdateProgressSummary()
    {
        if (_progressSummary == null || DataContext is not BibleReadingViewModel vm) return;

        var allBooks   = vm.OtBooks.Concat(vm.NtBooks).ToList();
        var totalChaps = allBooks.Sum(b => b.Chapters.Count);
        var readChaps  = allBooks.Sum(b => b.Chapters.Count(c => c.IsRead));
        _progressSummary.Text = $"{readChaps} of {totalChaps} chapters read";
    }

    // ── Chapter cell toggle ───────────────────────────────────────────────────

    private void OnChapterCellClick(object? sender, RoutedEventArgs e)
    {
        // IsRead is already updated by the TwoWay binding when IsChecked flips.
        // Just persist the new state and refresh the summary label.
        if (DataContext is BibleReadingViewModel vm)
            _ = vm.SaveAsync();
        UpdateProgressSummary();
    }

    // ── Close ─────────────────────────────────────────────────────────────────

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    // ── Free-pan (drag-to-scroll) ─────────────────────────────────────────────

    private void OnPanPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only pan with left mouse button or a single touch; ignore when a
        // chapter cell already handled the event (it calls e.Handled = true via
        // the Click event which fires before PointerPressed bubbles here).
        if (e.Handled) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        _panScrollViewer = this.FindControl<ScrollViewer>("PanScrollViewer");
        if (_panScrollViewer == null) return;

        _isPanning       = true;
        _panStartPointer = e.GetPosition(this);
        _panStartOffset  = _panScrollViewer.Offset;
        e.Pointer.Capture(_panScrollViewer);
    }

    private void OnPanPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || _panScrollViewer == null) return;

        var current = e.GetPosition(this);
        var delta   = _panStartPointer - current;
        var newX    = Math.Clamp(_panStartOffset.X + delta.X, 0,
            Math.Max(0, _panScrollViewer.Extent.Width  - _panScrollViewer.Viewport.Width));
        var newY    = Math.Clamp(_panStartOffset.Y + delta.Y, 0,
            Math.Max(0, _panScrollViewer.Extent.Height - _panScrollViewer.Viewport.Height));

        _panScrollViewer.Offset = new Vector(newX, newY);
    }

    private void OnPanPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        e.Pointer.Capture(null);
    }

    private void OnPanPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPanning = false;
    }
}
