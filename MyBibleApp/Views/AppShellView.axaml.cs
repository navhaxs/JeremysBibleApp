using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class AppShellView : UserControl
{
    private Grid?         _contentGrid;
    private GridSplitter? _paneSplitter;
    private MainView?     _primaryView;
    private MainView?     _secondaryView;
    private bool          _isSplit;

    // Split sizing policy
    private const double PreferredPaneMinWidth = 300;
    private const double AbsolutePaneMinWidth  = 180;
    private const double SplitterWidth         = 4;

    public AppShellView()
    {
        InitializeComponent();

        _contentGrid   = this.FindControl<Grid>("ContentGrid");
        _paneSplitter  = this.FindControl<GridSplitter>("PaneSplitter");
        _primaryView   = this.FindControl<MainView>("MainView");
        _secondaryView = this.FindControl<MainView>("SecondaryView");

        if (_primaryView != null) _primaryView.SplitToggled += OnSplitToggled;
        if (_secondaryView != null) _secondaryView.SplitToggled += OnSplitToggled;
        if (_contentGrid != null) _contentGrid.SizeChanged += OnContentGridSizeChanged;
    }

    // ── Split management ──────────────────────────────────────────────────────

    private void OnSplitToggled(object? sender, bool turnOn)
    {
        if (_contentGrid == null || _paneSplitter == null || _secondaryView == null) return;

        _isSplit = turnOn;

        if (_isSplit)
        {
            // Lazily create a fresh ViewModel for the secondary pane.
            if (_secondaryView.DataContext == null)
                _secondaryView.DataContext = new MainViewModel();

            _contentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _contentGrid.ColumnDefinitions[1].Width = new GridLength(SplitterWidth);
            _contentGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            _paneSplitter.IsVisible = true;
            _secondaryView.IsVisible = true;

            ApplySplitSizing();
        }
        else
        {
            _contentGrid.ColumnDefinitions[0].MinWidth = 120;
            _contentGrid.ColumnDefinitions[1].Width = new GridLength(0);
            _contentGrid.ColumnDefinitions[2].Width = new GridLength(0);
            _contentGrid.ColumnDefinitions[2].MinWidth = 0;
            _paneSplitter.IsVisible = false;
            _secondaryView.IsVisible = false;
        }

        // Keep both toggle buttons in sync.
        _primaryView?.SetSplitActive(_isSplit);
        _secondaryView?.SetSplitActive(_isSplit);
    }

    private void OnContentGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isSplit)
            ApplySplitSizing();
    }

    private void ApplySplitSizing()
    {
        if (_contentGrid == null) return;

        var col0 = _contentGrid.ColumnDefinitions[0];
        var col2 = _contentGrid.ColumnDefinitions[2];

        var available = Math.Max(0, _contentGrid.Bounds.Width - SplitterWidth);
        var half = available / 2.0;

        // Target 300px where possible; on narrow screens, reduce gracefully.
        var effectiveMin = Math.Min(PreferredPaneMinWidth, Math.Max(AbsolutePaneMinWidth, half));

        col0.MinWidth = effectiveMin;
        col2.MinWidth = effectiveMin;
    }

    // ── Debug view handlers ───────────────────────────────────────────────────

    private void OnDebugToggleClick(object? sender, RoutedEventArgs e)
    {
        var mainView        = this.FindControl<MainView>("MainView");
        var debugView       = this.FindControl<DebugPointerView>("DebugView");
        var debugDrawing    = this.FindControl<DebugDrawingView>("DebugDrawingView");
        var button          = (Button?)sender;

        if (mainView is null || debugView is null || debugDrawing is null || button is null) return;

        var showDebug   = !debugView.IsVisible;
        mainView.IsVisible  = !showDebug;
        debugView.IsVisible = showDebug;
        debugDrawing.IsVisible = false;
        button.Content = showDebug ? "Show Bible Reader" : "Show Debug Pointer (Dev)";
    }

    private void OnDebugDrawingToggleClick(object? sender, RoutedEventArgs e)
    {
        var mainView     = this.FindControl<MainView>("MainView");
        var debugView    = this.FindControl<DebugPointerView>("DebugView");
        var debugDrawing = this.FindControl<DebugDrawingView>("DebugDrawingView");
        var button       = (Button?)sender;

        if (mainView is null || debugDrawing is null || button is null) return;

        var showDebugDrawing = !debugDrawing.IsVisible;
        mainView.IsVisible    = !showDebugDrawing;
        if (debugView != null) debugView.IsVisible = false;
        debugDrawing.IsVisible = showDebugDrawing;
        button.Content = showDebugDrawing ? "Show Bible Reader" : "Show Debug Drawing (Dev)";
    }
}
