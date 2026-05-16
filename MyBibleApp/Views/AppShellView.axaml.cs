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
    private LocalStorageDebugView? _localStorageDebugView;
    private DebugLogsView? _debugLogsView;
    private BibleReadingView? _bibleReadingView;
    private readonly AppViewModel _appVM = new();
    private readonly List<ScriptureViewModel> _tabs = [];
    private readonly Dictionary<ScriptureViewModel, PropertyChangedEventHandler> _tabHeaderHandlers = [];
    private readonly Dictionary<ScriptureViewModel, InkOverlayCanvas.InkState?> _tabInkStates = [];
    private readonly Dictionary<ScriptureViewModel, double?> _tabScrollOffsets = [];
    private readonly Dictionary<ScriptureViewModel, (int Chapter, int Verse)> _tabVersePositions = [];
    private CancellationTokenSource? _persistTabsCts;
    private string _lastPersistedTabStateJson = string.Empty;
    private int _lastPersistedActiveIndex = -1;
    private int _activeTabIndex = -1;
    private bool _isRestoringTabs;

    // Sign-in overlay tracking
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
        _localStorageDebugView   = this.FindControl<LocalStorageDebugView>("LocalStorageDebugView");
        _debugLogsView           = this.FindControl<DebugLogsView>("DebugLogsView");
        _bibleReadingView = this.FindControl<BibleReadingView>("BibleReadingView");

        if (_bibleReadingView != null)
        {
            _bibleReadingView.DataContext    = new BibleReadingViewModel();
            _bibleReadingView.CloseRequested += OnBibleReadingCloseRequested;
            _bibleReadingView.ChapterNavigationRequested += OnBibleReadingChapterNavigationRequested;
        }

        SharedSyncRuntime.Instance.SyncCoordinator.SyncProgress += OnSyncProgress;

        DataContext = _appVM;

        // Give the secondary pane its own VM up front so it never inherits the
        // AppShell DataContext (which is the global AppViewModel).
        if (_secondaryView != null)
            _secondaryView.DataContext = new ScriptureViewModel(_appVM);

        _ = RestoreTabsAndAuthAsync();

        if (_primaryView != null) _primaryView.SplitToggled += OnSplitToggled;
        if (_secondaryView != null) _secondaryView.SplitToggled += OnSplitToggled;
        if (_primaryView != null) _primaryView.BibleReadingRequested += OnBibleReadingRequested;
        if (_contentGrid != null) _contentGrid.SizeChanged += OnContentGridSizeChanged;
        this.SizeChanged += OnShellSizeChanged;

        TrackAuthState();
    }

    private void AddTabInternal(ScriptureViewModel vm, bool makeActive)
    {
        _tabs.Add(vm);
        _tabInkStates[vm] = null;
        _tabScrollOffsets[vm] = null;

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(ScriptureViewModel.Header))
                RefreshTabButtons();

            if (args.PropertyName == nameof(ScriptureViewModel.Header)
                || args.PropertyName == nameof(ScriptureViewModel.SelectedLookupBook)
                || args.PropertyName == nameof(ScriptureViewModel.SelectedLookupChapter)
                || args.PropertyName == nameof(ScriptureViewModel.SelectedLookupVerse))
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

        // Save ink and verse position for the tab we're leaving.
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var leavingVm = _tabs[_activeTabIndex];
            _tabInkStates[leavingVm] = _primaryView.CaptureInkState();

            // Save the current chapter:verse (determined by header sync from visible paragraphs)
            // rather than raw scroll Y, because virtualizing panels map the same Y to different
            // items after an ItemsSource change.
            _tabVersePositions[leavingVm] = (
                Math.Max(1, leavingVm.SelectedLookupChapter),
                Math.Max(1, leavingVm.SelectedLookupVerse));
            _appVM.AppendSyncDebugLog($"[Tab] Leaving tab {_activeTabIndex} \"{leavingVm.Header}\" — saved position {leavingVm.SelectedLookupChapter}:{leavingVm.SelectedLookupVerse}");
        }

        _activeTabIndex = index;
        var vm = _tabs[index];

        // Capture the target verse BEFORE setting DataContext — the MainView's scroll-driven
        // header sync will overwrite the VM's chapter/verse with the outgoing tab's values.
        var targetPos = _tabVersePositions.TryGetValue(vm, out var saved)
            ? saved
            : (Chapter: Math.Max(1, vm.SelectedLookupChapter), Verse: Math.Max(1, vm.SelectedLookupVerse));

        // Suppress scroll-driven header sync BEFORE changing DataContext — otherwise
        // the outgoing tab's scroll position gets written into the incoming VM.
        _primaryView.SuppressScrollEvents();

        _primaryView.DataContext = vm;

        // Restore ink state and navigate to the saved verse position.
        _primaryView.RestoreInkState(_tabInkStates.TryGetValue(vm, out var inkState) ? inkState : null);
        _appVM.AppendSyncDebugLog($"[Tab] Entering tab {index} \"{vm.Header}\" — navigating to {targetPos.Chapter}:{targetPos.Verse}");
        _ = _primaryView.NavigateToVerseAndUnsuppressAsync(targetPos.Chapter, targetPos.Verse);

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
            button.SetValue(InputElement.IsHoldingEnabledProperty, true);
            button.AddHandler(InputElement.HoldingEvent, OnTabButtonHolding);
            button.AddHandler(PointerPressedEvent, OnTabButtonRightClick);
            ToolTip.SetTip(button, vm.Header);

            if (i == _activeTabIndex)
                button.Classes.Add("selected");

            button.Click += OnTabButtonClick;
            _tabButtonsHost.Children.Add(button);
        }
    }

    private static string GetTabLabel(ScriptureViewModel vm, int index)
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

    private void OnTabButtonHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started) return;
        if (sender is not Button button) return;

        // Show the flyout
        button.Flyout?.ShowAt(button);
        e.Handled = true;

        // Suppress the next Click so lifting the finger doesn't dismiss the flyout
        // by navigating/re-rendering. We attach a one-shot handler.
        void SuppressClick(object? s, RoutedEventArgs args)
        {
            args.Handled = true;
            button.Click -= SuppressClick;
        }
        button.Click += SuppressClick;
    }

    private void OnTabButtonRightClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button button) return;
        if (e.GetCurrentPoint(button).Properties.IsRightButtonPressed)
        {
            button.Flyout?.ShowAt(button);
            e.Handled = true;
        }
    }

    private void OnAddTabButtonClick(object? sender, RoutedEventArgs e)
    {
        AddTabInternal(new ScriptureViewModel(_appVM), makeActive: true);
    }

    // ── Sign-in overlay ───────────────────────────────────────────────────────

    private void TrackAuthState()
    {
        _authStateHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(AppViewModel.IsAuthenticating))
                Dispatcher.UIThread.Post(UpdateSignInOverlayVisibility);
        };

        _appVM.PropertyChanged += _authStateHandler;
        UpdateSignInOverlayVisibility();
    }

    private void UpdateSignInOverlayVisibility()
    {
        var overlay = this.FindControl<Panel>("SignInProgressOverlay");
        if (overlay != null)
            overlay.IsVisible = _appVM.IsAuthenticating;
    }

    private void OnCancelSignInButtonClick(object? sender, RoutedEventArgs e)
    {
        _appVM.CancelAuthentication();
    }

    private void OnReopenBrowserButtonClick(object? sender, RoutedEventArgs e)
    {
        _appVM.ReopenAuthBrowser();
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
        var moveUpButton = new Button
        {
            Content = "Move tab up",
            MinWidth = 120,
            IsEnabled = tabIndex > 0
        };

        var moveDownButton = new Button
        {
            Content = "Move tab down",
            MinWidth = 120,
            IsEnabled = tabIndex < _tabs.Count - 1
        };

        var duplicateButton = new Button
        {
            Content = "Duplicate tab",
            MinWidth = 120
        };

        var closeButton = new Button
        {
            Content = "Close tab",
            MinWidth = 120,
            IsEnabled = _tabs.Count > 1
        };

        var panel = new StackPanel
        {
            Spacing = 4,
            Children = { moveUpButton, moveDownButton, duplicateButton, closeButton }
        };

        var flyout = new Flyout
        {
            Placement = PlacementMode.Left,
            Content = panel
        };

        moveUpButton.Click += (_, _) =>
        {
            MoveTab(tabIndex, tabIndex - 1);
            flyout.Hide();
        };

        moveDownButton.Click += (_, _) =>
        {
            MoveTab(tabIndex, tabIndex + 1);
            flyout.Hide();
        };

        duplicateButton.Click += (_, _) =>
        {
            DuplicateTab(tabIndex);
            flyout.Hide();
        };

        closeButton.Click += (_, _) =>
        {
            CloseTab(tabIndex);
            flyout.Hide();
        };

        return flyout;
    }

    private void DuplicateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count)
            return;

        var source = _tabs[index];
        var newVm = new ScriptureViewModel(_appVM);

        var state = new OpenTabReferenceState
        {
            TabIndex = _tabs.Count,
            Header = source.Header,
            BookCode = source.SelectedLookupBook?.Code ?? source.BookCode,
            Chapter = Math.Max(1, source.SelectedLookupChapter),
            Verse = Math.Max(1, source.SelectedLookupVerse)
        };

        ApplyPersistedTabReference(newVm, state);
        AddTabInternal(newVm, makeActive: true);
    }

    private void MoveTab(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex)
            return;
        if (fromIndex < 0 || fromIndex >= _tabs.Count)
            return;
        if (toIndex < 0 || toIndex >= _tabs.Count)
            return;

        // Swap the two tab entries and all associated per-tab state.
        (_tabs[fromIndex], _tabs[toIndex]) = (_tabs[toIndex], _tabs[fromIndex]);

        // Swap ink states.
        (_tabInkStates[_tabs[fromIndex]], _tabInkStates[_tabs[toIndex]])
            = (_tabInkStates[_tabs[toIndex]], _tabInkStates[_tabs[fromIndex]]);

        // Swap scroll offsets.
        (_tabScrollOffsets[_tabs[fromIndex]], _tabScrollOffsets[_tabs[toIndex]])
            = (_tabScrollOffsets[_tabs[toIndex]], _tabScrollOffsets[_tabs[fromIndex]]);

        // Swap saved verse positions (only if both keys exist).
        var fromHasPos = _tabVersePositions.TryGetValue(_tabs[fromIndex], out var fromPos);
        var toHasPos   = _tabVersePositions.TryGetValue(_tabs[toIndex],   out var toPos);
        if (fromHasPos) _tabVersePositions[_tabs[toIndex]]   = fromPos;
        else            _tabVersePositions.Remove(_tabs[toIndex]);
        if (toHasPos)   _tabVersePositions[_tabs[fromIndex]] = toPos;
        else            _tabVersePositions.Remove(_tabs[fromIndex]);

        // Keep the active index tracking the same VM.
        if (_activeTabIndex == fromIndex)
            _activeTabIndex = toIndex;
        else if (_activeTabIndex == toIndex)
            _activeTabIndex = fromIndex;

        RefreshTabButtons();
        if (!_isRestoringTabs)
            RequestPersistOpenTabReferences();
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
        _tabScrollOffsets.Remove(vm);
        _tabVersePositions.Remove(vm);
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
            _primaryView.SuppressScrollEvents();
            _primaryView.DataContext = activeVm;
            _primaryView.RestoreInkState(_tabInkStates.TryGetValue(activeVm, out var inkState) ? inkState : null);
            var pos = _tabVersePositions.TryGetValue(activeVm, out var saved)
                ? saved
                : (Chapter: Math.Max(1, activeVm.SelectedLookupChapter), Verse: Math.Max(1, activeVm.SelectedLookupVerse));
            _appVM.AppendSyncDebugLog($"[Tab] After close, activating tab {_activeTabIndex} \"{activeVm.Header}\" — navigating to {pos.Chapter}:{pos.Verse}");
            _ = _primaryView.NavigateToVerseAndUnsuppressAsync(pos.Chapter, pos.Verse);
        }

        RefreshTabButtons();
        if (!_isRestoringTabs)
            RequestPersistOpenTabReferences();
    }

    private async Task RestoreTabsAndAuthAsync()
    {
        // Load persisted debug mode state early so the overlay is visible during restore.
        await _appVM.LoadDebugModeFromStorageAsync();

        // Load persisted theme and apply it.
        await _appVM.LoadThemeFromStorageAsync();
        var theme = Models.AppTheme.GetById(_appVM.SelectedThemeId);
        _primaryView?.ApplyTheme(theme);

        var overlay = this.FindControl<Panel>("StartupOverlay");

        _isRestoringTabs = true;
        _appVM.SuppressReadingProgressSync = true;
        try
        {
            // 1. Restore tabs from local storage immediately — no auth or network needed.
            _appVM.AppendSyncDebugLog("[Tabs] Loading persisted tab references...");
            var (persistedTabs, persistedActiveIndex) = await _appVM.LoadPersistedOpenTabReferencesAsync();
            if (persistedTabs.Count > 0)
            {
                _appVM.AppendSyncDebugLog($"[Tabs] Found {persistedTabs.Count} persisted tab(s), active index={persistedActiveIndex}");

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
                    var vm = new ScriptureViewModel(_appVM);
                    ApplyPersistedTabReference(vm, state);
                    AddTabInternal(vm, makeActive: false);
                    _appVM.AppendSyncDebugLog($"[Tabs] Restored tab: \"{vm.Header}\" (book={state.BookCode}, ch={state.Chapter}, v={state.Verse})");
                }

                if (_tabs.Count > 0)
                    SelectTab(Math.Clamp(persistedActiveIndex, 0, _tabs.Count - 1));

                // Load Bible content for each restored tab. Active tab is awaited so
                // content is visible as soon as the overlay hides; background tabs
                // load concurrently without blocking.
                var activeIdx = Math.Clamp(persistedActiveIndex, 0, _tabs.Count - 1);
                var orderedStates = persistedTabs.OrderBy(t => t.TabIndex).ToList();
                _appVM.AppendSyncDebugLog($"[Tabs] Loading content for {_tabs.Count} tab(s), active={activeIdx}...");
                var contentTasks = _tabs
                    .Select(vm =>
                    {
                        var code    = vm.SelectedLookupBook?.Code ?? "JHN";
                        var chapter = Math.Max(1, vm.SelectedLookupChapter);
                        var verse   = Math.Max(1, vm.SelectedLookupVerse);
                        return vm.TryLoadBookFromApiAsync(code, chapter, verse);
                    })
                    .ToList();
                await contentTasks[activeIdx]; // active tab ready before overlay hides
                _appVM.AppendSyncDebugLog("[Tabs] Active tab content loaded");

                // Navigate to the persisted verse so the user lands where they left off.
                // Use the original persisted state values (not the VM, which may have been
                // reset by TryLoadBookFromApiAsync).
                var activeState = orderedStates[activeIdx];
                var ch = Math.Max(1, activeState.Chapter);
                var vs = Math.Max(1, activeState.Verse);
                if (_primaryView != null)
                {
                    _appVM.AppendSyncDebugLog($"[Tabs] Navigating active tab to {ch}:{vs}");
                    await _primaryView.NavigateToVerseAsync(ch, vs);
                }

                _ = Task.WhenAll(contentTasks); // background tabs continue
            }
            else
            {
                _appVM.AppendSyncDebugLog("[Tabs] No persisted tabs found, creating default (Genesis 1:1)");
                var defaultVm = new ScriptureViewModel(_appVM);
                AddTabInternal(defaultVm, makeActive: true);
                await defaultVm.TryLoadBookFromApiAsync("GEN", 1, 1);
            }

            // Tabs are visible — dismiss the overlay so the user sees the app immediately.
            _isRestoringTabs = false;
            if (overlay != null) overlay.IsVisible = false;

            // 2. Auth + Drive pull happen silently in the background.

            // Smooth UX on reopen: this uses cached OAuth token when available.
            await _appVM.TryAutoAuthenticateOnStartupAsync();

            // If silent auth failed but there was a previous session, offer to re-sign-in.
            if (!_appVM.IsAuthenticated && await _appVM.HasPreviousAuthenticationAsync())
            {
                // Re-surface the overlay only for the interactive re-sign-in prompt.
                if (overlay != null) overlay.IsVisible = true;
                var shouldSignIn = await ShowStartupReSignInPromptAsync(
                    "Your previous sign-in has expired.");
                if (shouldSignIn)
                    await _appVM.AuthenticateAsync();
            }

            if (_appVM.IsAuthenticated)
            {
                var pullResult = await _appVM.PullFromDriveAsync();
                if (pullResult.BibleReadingProgress != null
                    && _bibleReadingView?.DataContext is BibleReadingViewModel brVm)
                {
                    brVm.ApplyRemoteSnapshot(pullResult.BibleReadingProgress);
                }
            }
        }
        finally
        {
            // Always re-enable normal persistence tracking and hide the overlay,
            // even if we returned early or an exception occurred.
            _isRestoringTabs = false;
            _appVM.SuppressReadingProgressSync = false;
            if (overlay != null)
                overlay.IsVisible = false;
        }
    }

    private static void ApplyPersistedTabReference(ScriptureViewModel vm, OpenTabReferenceState state)
    {
        var book = vm.LookupBooks.FirstOrDefault(b =>
            string.Equals(b.Code, state.BookCode, StringComparison.OrdinalIgnoreCase));

        vm.RestoreLookupPosition(book, Math.Max(1, state.Chapter), Math.Max(1, state.Verse));

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

        _appVM.AppendSyncDebugLog($"[Tabs] Persisting {refs.Count} tab(s), active={activeIndex}: {string.Join(", ", refs.Select(r => $"\"{r.Header}\" ({r.BookCode} {r.Chapter}:{r.Verse})"))}");
        await _appVM.PersistOpenTabReferencesAsync(refs, activeIndex);

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

    public bool HasAuthenticatedTabs() => _appVM.IsAuthenticated;

    public Task ForcePersistTabsAsync() => PersistOpenTabReferencesAsync();

    public async Task ShutdownAsync()
    {
        // Always persist tab state locally before exit.
        await PersistOpenTabReferencesAsync().ConfigureAwait(false);

        if (_appVM.IsAuthenticated)
            await _appVM.ForceSyncAsync().ConfigureAwait(false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SharedSyncRuntime.Instance.SyncCoordinator.SyncProgress -= OnSyncProgress;

        if (_authStateHandler != null)
            _appVM.PropertyChanged -= _authStateHandler;

        _appVM.Dispose();
        _persistTabsCts?.Cancel();
        _persistTabsCts?.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    // ── Bible Reading overlay ─────────────────────────────────────────────────

    private void OnBibleReadingRequested(object? sender, EventArgs e)
    {
        if (_bibleReadingView == null) return;

        // Highlight the chapter currently viewed in the active tab
        if (_bibleReadingView.DataContext is BibleReadingViewModel brVm
            && _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var activeTab = _tabs[_activeTabIndex];
            var bookCode = activeTab.SelectedLookupBook?.Code ?? activeTab.BookCode;
            brVm.SetCurrentChapter(bookCode, activeTab.SelectedLookupChapter);
        }

        _bibleReadingView.IsVisible = true;
    }

    private void OnBibleReadingCloseRequested(object? sender, EventArgs e)
    {
        if (_bibleReadingView == null) return;
        _bibleReadingView.IsVisible = false;
    }

    private async void OnBibleReadingChapterNavigationRequested(object? sender, ChapterNavigationEventArgs e)
    {
        // Close the Bible Reading overlay
        if (_bibleReadingView != null)
            _bibleReadingView.IsVisible = false;

        // Navigate the active tab to the requested book and chapter
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count || _primaryView == null) return;
        var vm = _tabs[_activeTabIndex];
        var result = await vm.TryLoadBookFromApiAsync(e.BookCode, e.Chapter, 1);
        if (result.Success)
            await _primaryView.NavigateToVerseAsync(e.Chapter, 1);
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

    private void SetDebugSurface(bool showPointer, bool showDrawing, bool showSync, bool showThemeResources = false, bool showLocalStorage = false, bool showDebugLogs = false)
    {
        if (_primaryView is null || _debugPointerView is null || _debugDrawingView is null || _syncDebugView is null)
            return;

        bool showOverlay = showPointer || showDrawing || showSync || showThemeResources || showLocalStorage || showDebugLogs;
        _primaryView.IsVisible = !showOverlay;
        _debugPointerView.IsVisible = showPointer;
        _debugDrawingView.IsVisible = showDrawing;
        _syncDebugView.IsVisible = showSync;
        if (_themeResourcesDebugView != null)
            _themeResourcesDebugView.IsVisible = showThemeResources;
        if (_localStorageDebugView != null)
            _localStorageDebugView.IsVisible = showLocalStorage;
        if (_debugLogsView != null)
            _debugLogsView.IsVisible = showDebugLogs;
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
                await _appVM.RefreshSyncDebugDataAsync();
                break;
            case 4:
                SetDebugSurface(showPointer: false, showDrawing: false, showSync: false, showThemeResources: true);
                _themeResourcesDebugView?.Refresh();
                break;
            case 5:
                SetDebugSurface(showPointer: false, showDrawing: false, showSync: false, showLocalStorage: true);
                _ = _localStorageDebugView?.RefreshAsync();
                break;
            case 6:
                SetDebugSurface(showPointer: false, showDrawing: false, showSync: false, showDebugLogs: true);
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
