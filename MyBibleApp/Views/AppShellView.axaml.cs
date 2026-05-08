using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MyBibleApp.Controls;
using MyBibleApp.Models;
using MyBibleApp.Services;
using MyBibleApp.Services.Sync;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class AppShellView : UserControl
{
    private Grid?             _contentGrid;
    private GridSplitter?     _paneSplitter;
    private MainView?         _primaryView;
    private MainView?         _secondaryView;
    private StackPanel?       _tabButtonsHost;
    private bool              _isSplit;
    private DebugPointerView? _debugPointerView;
    private DebugDrawingView? _debugDrawingView;
    private SyncDebugView?    _syncDebugView;
    private ThemeResourcesDebugView? _themeResourcesDebugView;
    private BibleReadingView? _bibleReadingView;
    private readonly List<MainViewModel> _tabs = [];
    private readonly Dictionary<MainViewModel, PropertyChangedEventHandler> _tabHeaderHandlers = [];
    private readonly Dictionary<MainViewModel, InkOverlayCanvas.InkState?> _tabInkStates = [];
    private CancellationTokenSource? _persistTabsCts;
    private string _lastPersistedTabStateJson = string.Empty;
    private int _lastPersistedActiveIndex = -1;
    private int _activeTabIndex = -1;
    private bool _isRestoringTabs;

    // Sign-in overlay tracking
    private MainViewModel? _trackedAuthVm;
    private PropertyChangedEventHandler? _authStateHandler;
    private TaskCompletionSource<bool>? _startupSignInPromptTcs;

    // Split sizing policy
    private const double PreferredPaneMinWidth = 300;
    private const double AbsolutePaneMinWidth  = 180;
    private const double SplitterWidth         = 4;
    private const double TabBarMinWidth        = 600;
    private const int TabStatePersistDebounceMilliseconds = 300;
    private Border?      _tabBar;

    public AppShellView()
    {
        InitializeComponent();

        _contentGrid    = this.FindControl<Grid>("ContentGrid");
        _paneSplitter   = this.FindControl<GridSplitter>("PaneSplitter");
        _primaryView    = this.FindControl<MainView>("MainView");
        _secondaryView  = this.FindControl<MainView>("SecondaryView");
        _tabButtonsHost = this.FindControl<StackPanel>("TabButtonsHost");
        _tabBar         = this.FindControl<Border>("TabBar");
        _debugPointerView = this.FindControl<DebugPointerView>("DebugView");
        _debugDrawingView = this.FindControl<DebugDrawingView>("DebugDrawingView");
        _syncDebugView    = this.FindControl<SyncDebugView>("SyncDebugView");
        _themeResourcesDebugView = this.FindControl<ThemeResourcesDebugView>("ThemeResourcesDebugView");
        _bibleReadingView = this.FindControl<BibleReadingView>("BibleReadingView");

        if (_bibleReadingView != null)
        {
            _bibleReadingView.DataContext    = new BibleReadingViewModel();
            _bibleReadingView.CloseRequested += OnBibleReadingCloseRequested;
        }

        SharedSyncRuntime.Instance.SyncCoordinator.SyncProgress += OnSyncProgress;

        // Give the secondary pane its own VM up front so it never inherits the
        // AppShell DataContext (which is used by the primary pane/debug UI).
        if (_secondaryView != null)
            _secondaryView.DataContext = new MainViewModel();

        InitializeTabs();
        _ = RestoreTabsAndAuthAsync();

        if (_primaryView != null) _primaryView.SplitToggled += OnSplitToggled;
        if (_secondaryView != null) _secondaryView.SplitToggled += OnSplitToggled;
        if (_primaryView != null) _primaryView.BibleReadingRequested += OnBibleReadingRequested;
        if (_contentGrid != null) _contentGrid.SizeChanged += OnContentGridSizeChanged;
        this.SizeChanged += OnShellSizeChanged;
    }

    private void InitializeTabs()
    {
        var initialVm = DataContext as MainViewModel ?? new MainViewModel();
        AddTabInternal(initialVm, makeActive: true);
    }

    private void AddTabInternal(MainViewModel vm, bool makeActive)
    {
        _tabs.Add(vm);
        _tabInkStates[vm] = null;

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Header))
                RefreshTabButtons();

            if (args.PropertyName == nameof(MainViewModel.Header)
                || args.PropertyName == nameof(MainViewModel.SelectedLookupBook)
                || args.PropertyName == nameof(MainViewModel.SelectedLookupChapter)
                || args.PropertyName == nameof(MainViewModel.SelectedLookupVerse))
            {
                if (!_isRestoringTabs)
                    RequestPersistOpenTabReferences();
            }
        };

        vm.PropertyChanged += handler;
        _tabHeaderHandlers[vm] = handler;

        RefreshTabButtons();

        if (makeActive)
            SelectTab(_tabs.Count - 1);

        if (!_isRestoringTabs)
            RequestPersistOpenTabReferences();
    }

    private void SelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Count || _primaryView == null)
            return;

        // Save ink state for the tab we're leaving.
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabInkStates[_tabs[_activeTabIndex]] = _primaryView.CaptureInkState();

        _activeTabIndex = index;
        var vm = _tabs[index];
        DataContext = vm;
        _primaryView.DataContext = vm;

        // Restore ink state for the tab we're entering.
        _primaryView.RestoreInkState(_tabInkStates.TryGetValue(vm, out var inkState) ? inkState : null);

        TrackAuthStateOf(vm);
        RefreshTabButtons();
        if (!_isRestoringTabs)
            RequestPersistOpenTabReferences();
    }

    private void RefreshTabButtons()
    {
        if (_tabButtonsHost == null)
            return;

        _tabButtonsHost.Children.Clear();

        for (var i = 0; i < _tabs.Count; i++)
        {
            var vm = _tabs[i];

            var rotatedLabel = new LayoutTransformControl
            {
                LayoutTransform = new RotateTransform(-90),
                Child = new TextBlock
                {
                    Text = GetTabLabel(vm, i),
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            };

            var button = new Button
            {
                Classes = { "passage-tab" },
                Tag = i,
                Content = rotatedLabel
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

    // ── Sign-in overlay ───────────────────────────────────────────────────────

    private void TrackAuthStateOf(MainViewModel? vm)
    {
        if (_trackedAuthVm != null && _authStateHandler != null)
            _trackedAuthVm.PropertyChanged -= _authStateHandler;

        _trackedAuthVm = vm;
        _authStateHandler = vm == null ? null : (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsAuthenticating))
                Dispatcher.UIThread.Post(UpdateSignInOverlayVisibility);
        };

        if (vm != null && _authStateHandler != null)
            vm.PropertyChanged += _authStateHandler;

        UpdateSignInOverlayVisibility();
    }

    private void UpdateSignInOverlayVisibility()
    {
        var overlay = this.FindControl<Panel>("SignInProgressOverlay");
        if (overlay != null)
            overlay.IsVisible = _trackedAuthVm?.IsAuthenticating == true;
    }

    private void OnCancelSignInButtonClick(object? sender, RoutedEventArgs e)
    {
        _trackedAuthVm?.CancelAuthentication();
    }

    private void OnReopenBrowserButtonClick(object? sender, RoutedEventArgs e)
    {
        _trackedAuthVm?.ReopenAuthBrowser();
    }

    // ── Startup re-sign-in prompt ─────────────────────────────────────────────

    private void OnStartupSignInAgainButtonClick(object? sender, RoutedEventArgs e)
    {
        var panel = this.FindControl<StackPanel>("StartupReSignInPanel");
        if (panel != null) panel.IsVisible = false;
        _startupSignInPromptTcs?.TrySetResult(true);
    }

    private void OnStartupContinueWithoutSignInButtonClick(object? sender, RoutedEventArgs e)
    {
        _startupSignInPromptTcs?.TrySetResult(false);
    }

    private Task<bool> ShowStartupReSignInPromptAsync(string detail)
    {
        UpdateStartupOverlay("Session expired", detail, 100);
        var progressBar = this.FindControl<ProgressBar>("StartupOverlayProgress");
        if (progressBar != null) progressBar.IsVisible = false;
        var panel = this.FindControl<StackPanel>("StartupReSignInPanel");
        if (panel != null) panel.IsVisible = true;
        _startupSignInPromptTcs = new TaskCompletionSource<bool>();
        return _startupSignInPromptTcs.Task;
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

        _tabInkStates.Remove(vm);
        _tabs.RemoveAt(index);
        vm.Dispose();

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
            _primaryView.RestoreInkState(_tabInkStates.TryGetValue(activeVm, out var inkState) ? inkState : null);
        }

        RefreshTabButtons();
        if (!_isRestoringTabs)
            RequestPersistOpenTabReferences();
    }

    private async Task RestoreTabsAndAuthAsync()
    {
        if (_tabs.Count == 0)
            return;

        var overlay = this.FindControl<Panel>("StartupOverlay");

        // Suppress persistence-to-queue for the entire startup sequence.
        _isRestoringTabs = true;
        foreach (var vm in _tabs) vm.SetReadingProgressSyncSuppressed(true);
        try
        {
            var ownerVm = _tabs[Math.Clamp(_activeTabIndex, 0, _tabs.Count - 1)];

            UpdateStartupOverlay("Loading...", "Checking saved sign-in...", 0);

            // Smooth UX on reopen: this uses cached OAuth token when available.
            await ownerVm.TryAutoAuthenticateOnStartupAsync();

            // If silent auth failed but there was a previous session, offer to re-sign-in.
            if (!ownerVm.IsAuthenticated && await ownerVm.HasPreviousAuthenticationAsync())
            {
                var shouldSignIn = await ShowStartupReSignInPromptAsync(
                    "Your previous sign-in has expired.");
                if (shouldSignIn)
                    await ownerVm.AuthenticateAsync();
            }

            if (ownerVm.IsAuthenticated)
            {
                UpdateStartupOverlay("Loading...", "Syncing with Google Drive...", 0);

                var pullResult = await ownerVm.PullFromDriveAsync();
                if (pullResult.BibleReadingProgress != null
                    && _bibleReadingView?.DataContext is BibleReadingViewModel brVm)
                {
                    brVm.ApplyRemoteSnapshot(pullResult.BibleReadingProgress);
                }
            }

            UpdateStartupOverlay("Loading...", "Restoring your open tabs...", 100);

            var (persistedTabs, persistedActiveIndex) = await ownerVm.LoadPersistedOpenTabReferencesAsync();
            if (persistedTabs.Count == 0)
                return;

            foreach (var vm in _tabs)
            {
                if (_tabHeaderHandlers.TryGetValue(vm, out var handler))
                    vm.PropertyChanged -= handler;

                vm.Dispose();
            }

            _tabHeaderHandlers.Clear();
            _tabs.Clear();
            _tabInkStates.Clear();
            _activeTabIndex = -1;

            foreach (var state in persistedTabs.OrderBy(t => t.TabIndex))
            {
                var vm = new MainViewModel();
                vm.SetReadingProgressSyncSuppressed(true);
                ApplyPersistedTabReference(vm, state);
                AddTabInternal(vm, makeActive: false);
            }

            if (_tabs.Count > 0)
                SelectTab(Math.Clamp(persistedActiveIndex, 0, _tabs.Count - 1));

            // No PersistOpenTabReferencesAsync() here — local storage is already
            // correct after the Drive pull + tab restore. Calling it would enqueue
            // preferences identical to what's on Drive, causing a spurious upload
            // on the next app launch.
        }
        finally
        {
            // Always re-enable normal persistence tracking and hide the overlay,
            // even if we returned early or an exception occurred.
            _isRestoringTabs = false;
            foreach (var vm in _tabs) vm.SetReadingProgressSyncSuppressed(false);
            if (overlay != null)
                overlay.IsVisible = false;
        }
    }

    private static void ApplyPersistedTabReference(MainViewModel vm, OpenTabReferenceState state)
    {
        var book = vm.LookupBooks.FirstOrDefault(b =>
            string.Equals(b.Code, state.BookCode, StringComparison.OrdinalIgnoreCase));

        if (book != null)
            vm.SelectedLookupBook = book;

        vm.UpdateLookupFromReaderProgress(Math.Max(1, state.Chapter), Math.Max(1, state.Verse));

        if (!string.IsNullOrWhiteSpace(state.Header))
            vm.Header = state.Header;
    }

    private async Task PersistOpenTabReferencesAsync()
    {
        if (_tabs.Count == 0)
            return;

        var refs = new List<OpenTabReferenceState>(_tabs.Count);
        for (var i = 0; i < _tabs.Count; i++)
        {
            var vm = _tabs[i];
            refs.Add(new OpenTabReferenceState
            {
                TabIndex = i,
                Header = vm.Header,
                BookCode = vm.SelectedLookupBook?.Code ?? vm.BookCode,
                Chapter = Math.Max(1, vm.SelectedLookupChapter),
                Verse = Math.Max(1, vm.SelectedLookupVerse)
            });
        }

        var activeIndex = _activeTabIndex >= 0 ? _activeTabIndex : 0;
        var tabStateJson = JsonSerializer.Serialize(refs);
        if (tabStateJson == _lastPersistedTabStateJson && activeIndex == _lastPersistedActiveIndex)
            return;

        var ownerVm = _tabs[Math.Clamp(activeIndex, 0, _tabs.Count - 1)];
        await ownerVm.PersistOpenTabReferencesAsync(refs, activeIndex);

        _lastPersistedTabStateJson = tabStateJson;
        _lastPersistedActiveIndex = activeIndex;
    }

    private void RequestPersistOpenTabReferences()
    {
        _persistTabsCts?.Cancel();
        _persistTabsCts?.Dispose();

        _persistTabsCts = new CancellationTokenSource();
        var cancellationToken = _persistTabsCts.Token;

        _ = Task.Delay(TabStatePersistDebounceMilliseconds, cancellationToken).ContinueWith(async _ =>
        {
            if (!cancellationToken.IsCancellationRequested)
                await PersistOpenTabReferencesAsync();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public bool HasAuthenticatedTabs() => _tabs.Any(vm => vm.IsAuthenticated);

    public async Task ShutdownAsync()
    {
        var ownerVm = _tabs.FirstOrDefault(vm => vm.IsAuthenticated);
        if (ownerVm != null)
            await ownerVm.ForceSyncAsync().ConfigureAwait(false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SharedSyncRuntime.Instance.SyncCoordinator.SyncProgress -= OnSyncProgress;
        _persistTabsCts?.Cancel();
        _persistTabsCts?.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    // ── Bible Reading overlay ─────────────────────────────────────────────────

    private void OnBibleReadingRequested(object? sender, EventArgs e)
    {
        if (_bibleReadingView == null) return;
        _bibleReadingView.IsVisible = true;
    }

    private void OnBibleReadingCloseRequested(object? sender, EventArgs e)
    {
        if (_bibleReadingView == null) return;
        _bibleReadingView.IsVisible = false;
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

    private void OnShellSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_tabBar != null)
            _tabBar.IsVisible = e.NewSize.Width >= TabBarMinWidth;
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

    private void SetDebugSurface(bool showPointer, bool showDrawing, bool showSync, bool showThemeResources = false)
    {
        if (_primaryView is null || _debugPointerView is null || _debugDrawingView is null || _syncDebugView is null)
            return;

        bool showOverlay = showPointer || showDrawing || showSync || showThemeResources;
        _primaryView.IsVisible = !showOverlay;
        _debugPointerView.IsVisible = showPointer;
        _debugDrawingView.IsVisible = showDrawing;
        _syncDebugView.IsVisible = showSync;
        if (_themeResourcesDebugView != null)
            _themeResourcesDebugView.IsVisible = showThemeResources;
    }

    private async void OnDebugTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabStrip tabStrip) return;

        switch (tabStrip.SelectedIndex)
        {
            case 1:
                SetDebugSurface(showPointer: true, showDrawing: false, showSync: false);
                break;
            case 2:
                SetDebugSurface(showPointer: false, showDrawing: true, showSync: false);
                break;
            case 3:
                SetDebugSurface(showPointer: false, showDrawing: false, showSync: true);
                if (DataContext is MainViewModel vm)
                    await vm.RefreshSyncDebugDataAsync();
                break;
            case 4:
                SetDebugSurface(showPointer: false, showDrawing: false, showSync: false, showThemeResources: true);
                _themeResourcesDebugView?.Refresh();
                break;
            default:
                SetDebugSurface(showPointer: false, showDrawing: false, showSync: false);
                break;
        }
    }

    private void OnSyncProgress(object? sender, SyncProgressEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var startupOverlay = this.FindControl<Panel>("StartupOverlay");
            if (startupOverlay?.IsVisible == true)
                UpdateStartupOverlay("Loading...", e.Message, e.Progress);
        });
    }

    private void UpdateStartupOverlay(string title, string detail, int progress)
    {
        var titleText = this.FindControl<TextBlock>("StartupOverlayMessage");
        var detailText = this.FindControl<TextBlock>("StartupOverlayDetail");
        var progressBar = this.FindControl<ProgressBar>("StartupOverlayProgress");

        if (titleText != null)
            titleText.Text = title;

        if (detailText != null)
            detailText.Text = string.IsNullOrWhiteSpace(detail) ? "Preparing local data..." : detail;

        if (progressBar != null)
        {
            progressBar.IsIndeterminate = progress <= 0 || progress >= 100;
            progressBar.Value = progress;
        }
    }
}
