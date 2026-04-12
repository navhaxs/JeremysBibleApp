using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class AppShellView : UserControl
{
    private Grid?         _contentGrid;
    private GridSplitter? _paneSplitter;
    private MainView?     _primaryView;
    private MainView?     _secondaryView;
    private StackPanel?   _tabButtonsHost;
    private bool          _isSplit;
    private readonly List<MainViewModel> _tabs = [];
    private readonly Dictionary<MainViewModel, PropertyChangedEventHandler> _tabHeaderHandlers = [];
    private int _activeTabIndex = -1;

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
        _tabButtonsHost = this.FindControl<StackPanel>("TabButtonsHost");

        // Give the secondary pane its own VM up front so it never inherits the
        // AppShell DataContext (which is used by the primary pane/debug UI).
        if (_secondaryView != null)
            _secondaryView.DataContext = new MainViewModel();

        InitializeTabs();

        if (_primaryView != null) _primaryView.SplitToggled += OnSplitToggled;
        if (_secondaryView != null) _secondaryView.SplitToggled += OnSplitToggled;
        if (_contentGrid != null) _contentGrid.SizeChanged += OnContentGridSizeChanged;
    }

    private void InitializeTabs()
    {
        var initialVm = DataContext as MainViewModel ?? new MainViewModel();
        AddTabInternal(initialVm, makeActive: true);
    }

    private void AddTabInternal(MainViewModel vm, bool makeActive)
    {
        _tabs.Add(vm);

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Header))
                RefreshTabButtons();
        };

        vm.PropertyChanged += handler;
        _tabHeaderHandlers[vm] = handler;

        RefreshTabButtons();

        if (makeActive)
            SelectTab(_tabs.Count - 1);
    }

    private void SelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Count || _primaryView == null)
            return;

        _activeTabIndex = index;
        var vm = _tabs[index];
        DataContext = vm;
        _primaryView.DataContext = vm;
        RefreshTabButtons();
    }

    private void RefreshTabButtons()
    {
        if (_tabButtonsHost == null)
            return;

        _tabButtonsHost.Children.Clear();

        for (var i = 0; i < _tabs.Count; i++)
        {
            var vm = _tabs[i];
            var button = new Button
            {
                Classes = { "passage-tab" },
                Tag = i,
                Content = GetTabLabel(vm, i)
            };

            button.Flyout = CreateTabFlyout(i);
            button.Holding += OnTabButtonHolding;
            ToolTip.SetTip(button, vm.Header);

            if (i == _activeTabIndex)
                button.Classes.Add("selected");

            button.Click += OnTabButtonClick;
            _tabButtonsHost.Children.Add(button);
        }
    }

    private static string GetTabLabel(MainViewModel vm, int index)
    {
        var header = vm.Header;
        if (string.IsNullOrWhiteSpace(header))
            return $"Tab {index + 1}";

        return header.Length <= 16 ? header : header[..16] + "...";
    }

    private void OnTabButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        SelectTab(index);
    }

    private void OnAddTabButtonClick(object? sender, RoutedEventArgs e)
    {
        AddTabInternal(new MainViewModel(), makeActive: true);
    }

    private Flyout CreateTabFlyout(int tabIndex)
    {
        var closeButton = new Button
        {
            Content = "Close tab",
            MinWidth = 120,
            IsEnabled = _tabs.Count > 1
        };

        var flyout = new Flyout
        {
            Placement = PlacementMode.Left,
            Content = closeButton
        };

        closeButton.Click += (_, _) =>
        {
            CloseTab(tabIndex);
            flyout.Hide();
        };

        return flyout;
    }

    private void OnTabButtonHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        if (button.Flyout is Flyout flyout)
            flyout.ShowAt(button);

        e.Handled = true;
    }

    private void CloseTab(int index)
    {
        if (_tabs.Count <= 1 || index < 0 || index >= _tabs.Count)
            return;

        var vm = _tabs[index];
        if (_tabHeaderHandlers.TryGetValue(vm, out var handler))
        {
            vm.PropertyChanged -= handler;
            _tabHeaderHandlers.Remove(vm);
        }

        _tabs.RemoveAt(index);

        if (_activeTabIndex == index)
            _activeTabIndex = Math.Min(index, _tabs.Count - 1);
        else if (_activeTabIndex > index)
            _activeTabIndex--;

        if (_activeTabIndex < 0 && _tabs.Count > 0)
            _activeTabIndex = 0;

        if (_primaryView != null && _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var activeVm = _tabs[_activeTabIndex];
            DataContext = activeVm;
            _primaryView.DataContext = activeVm;
        }

        RefreshTabButtons();
    }

    // ── Split management ──────────────────────────────────────────────────────

    private void OnSplitToggled(object? sender, bool turnOn)
    {
        if (_contentGrid == null || _paneSplitter == null || _secondaryView == null) return;

        _isSplit = turnOn;

        if (_isSplit)
        {

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
