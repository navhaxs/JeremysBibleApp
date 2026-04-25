using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MyBibleApp.Services.Sync;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace MyBibleApp.Sync.Tests;

/// <summary>
/// Integration-style tests that exercise the full auth journey and the
/// app-resume sync flow end to end.  Auth and Drive services are mocked;
/// the queue manager, local storage, and coordinator are the real
/// implementations backed by temp directories.
/// </summary>
public sealed class AuthJourneyAndResumeSyncTests : IDisposable
{
    private readonly string _storageDir;
    private readonly string _queueDir;
    private readonly IGoogleDriveAuthService _authService;
    private readonly IGoogleDriveSyncService _syncService;
    private readonly INetworkStatusMonitor _networkMonitor;
    private readonly FileBasedLocalStorageProvider _localStorage;
    private readonly FileSyncQueueManager _queueManager;

    // Capture the connectivity-changed delegate so tests can raise it manually
    private NetworkStatusChangedEventHandler? _connectivityHandler;

    public AuthJourneyAndResumeSyncTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _storageDir = Path.Combine(Path.GetTempPath(), $"sync_storage_{id}");
        _queueDir = Path.Combine(Path.GetTempPath(), $"sync_queue_{id}");

        _authService = Substitute.For<IGoogleDriveAuthService>();
        _syncService = Substitute.For<IGoogleDriveSyncService>();

        _networkMonitor = Substitute.For<INetworkStatusMonitor>();
        _networkMonitor.IsConnected.Returns(true);

        // Capture ConnectivityChanged subscriptions so we can raise the event
        _networkMonitor.When(m => m.ConnectivityChanged += Arg.Any<NetworkStatusChangedEventHandler>())
            .Do(ci => _connectivityHandler = ci.Arg<NetworkStatusChangedEventHandler>());

        _localStorage = new FileBasedLocalStorageProvider(_storageDir);
        _queueManager = new FileSyncQueueManager(_queueDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, true);
        if (Directory.Exists(_queueDir)) Directory.Delete(_queueDir, true);
    }

    private SyncCoordinator CreateCoordinator() =>
        new(_authService, _syncService, _queueManager, _networkMonitor, _localStorage);

    // ═══════════════════════════════════════════════════════════════════
    //  1. Full authentication journey
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullAuthJourney_Authenticate_ThenSyncReadingProgress_ThenSignOut()
    {
        // ── Arrange: auth succeeds, sync succeeds ──────────────────────
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("access-token-abc", "user@bible.app"));
        _authService.IsAuthenticated.Returns(true);

        using var coordinator = CreateCoordinator();

        // ── Act 1: Authenticate ────────────────────────────────────────
        var authOk = await coordinator.AuthenticateAsync();
        Assert.True(authOk);

        // Verify the last authenticated user was persisted locally
        var savedUser = await _localStorage.GetAsync("LastAuthenticatedUser");
        Assert.Equal("user@bible.app", savedUser);

        // ── Act 2: Sync reading progress ───────────────────────────────
        var syncResult = await coordinator.SyncReadingProgressAsync("JHN", 3, 16);
        Assert.True(syncResult.IsSuccess);

        // Local storage should also have the reading progress
        var localProgress = await _localStorage.GetObjectAsync<ReadingProgressSnapshot>("CurrentReadingProgress");
        Assert.NotNull(localProgress);
        Assert.Equal("JHN", localProgress!.BookCode);
        Assert.Equal(3, localProgress.Chapter);
        Assert.Equal(16, localProgress.Verse);

        // Remote sync is deferred until a later force sync or reconnect.
        Assert.Equal(1, await _queueManager.GetPendingCountAsync());
        await _syncService.DidNotReceive().SyncReadingProgressAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());

        // ── Act 3: Sign out ────────────────────────────────────────────
        coordinator.SignOut();
        await _authService.Received(1).RevokeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  2. Auth failure → retry → success
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AuthJourney_FirstAttemptFails_RetrySucceeds()
    {
        // First call fails, second succeeds
        _authService.AuthenticateAsync()
            .Returns(
                AuthenticationResult.Failure("No credentials file found"),
                AuthenticationResult.Success("token-retry", "retry@bible.app"));

        using var coordinator = CreateCoordinator();

        // First attempt throws
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.AuthenticateAsync());
        Assert.Contains("No credentials file", ex.Message);

        // Retry succeeds
        _authService.IsAuthenticated.Returns(true);
        var ok = await coordinator.AuthenticateAsync();
        Assert.True(ok);
        Assert.Equal("retry@bible.app", await _localStorage.GetAsync("LastAuthenticatedUser"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3. Auto-auth on startup with cached credentials
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AutoAuth_CachedCredentials_AuthenticatesAndSyncsImmediately()
    {
        // Simulate cached token (auto-auth succeeds silently)
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("cached-token", "cached@bible.app"));
        _authService.IsAuthenticated.Returns(true);

        using var coordinator = CreateCoordinator();

        // App startup: auto-authenticate
        var authenticated = await coordinator.AuthenticateAsync();
        Assert.True(authenticated);

        // Immediately sync reading progress — no second auth prompt needed
        var r1 = await coordinator.SyncReadingProgressAsync("ROM", 8, 28);
        Assert.True(r1.IsSuccess);

        // And an annotation
        var annotation = new AnnotationBundle
        {
            BookCode = "ROM", Chapter = 8, Verse = 28,
            Notes = "All things work together for good",
            IsBookmarked = true
        };
        var r2 = await coordinator.SyncAnnotationAsync(annotation);
        Assert.True(r2.IsSuccess);

        // Both were queued for later remote sync.
        Assert.Equal(2, await _queueManager.GetPendingCountAsync());
        await _syncService.DidNotReceive().SyncReadingProgressAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
        await _syncService.DidNotReceive().SyncAnnotationAsync(Arg.Any<AnnotationBundle>());
    }

    // ═══════════════════════════════════════════════════════════════════
    //  4. Offline queuing → come online → ForceSync drains queue
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OfflineThenOnline_QueuesOperations_ForceSyncDrainsQueue()
    {
        // Start offline
        _networkMonitor.IsConnected.Returns(false);

        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("token", "user@bible.app"));
        _authService.IsAuthenticated.Returns(true);

        _syncService.SyncReadingProgressAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(SyncResult.Success(1));
        _syncService.SyncPreferencesAsync(Arg.Any<PreferencesSnapshot>())
            .Returns(SyncResult.Success(1));

        using var coordinator = CreateCoordinator();
        await coordinator.AuthenticateAsync();

        // ── While offline: sync operations get queued ──────────────────
        var r1 = await coordinator.SyncReadingProgressAsync("GEN", 1, 1);
        Assert.True(r1.IsSuccess); // queued locally

        var prefs = new PreferencesSnapshot { Theme = "Dark", FontSize = 18 };
        var r2 = await coordinator.SyncPreferencesAsync(prefs);
        Assert.True(r2.IsSuccess); // queued locally

        // Drive was NOT called
        await _syncService.DidNotReceive().SyncReadingProgressAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
        await _syncService.DidNotReceive().SyncPreferencesAsync(Arg.Any<PreferencesSnapshot>());

        // Queue has 2 items, local storage has the data
        Assert.Equal(2, await _queueManager.GetPendingCountAsync());
        Assert.NotNull(await _localStorage.GetAsync("CurrentReadingProgress"));
        Assert.NotNull(await _localStorage.GetAsync("UserPreferences"));

        // ── Come back online: ForceSync processes the queue ────────────
        _networkMonitor.IsConnected.Returns(true);
        // Simulate the coordinator seeing the network change
        // (the coordinator constructor wired ConnectivityChanged → _isOffline toggle)
        _connectivityHandler?.Invoke(true);

        // ForceSync runs on a background thread — give it time to process
        coordinator.ForceSync();
        await Task.Delay(1000);

        // Now the Drive service should have been called for both items
        await _syncService.Received().SyncReadingProgressAsync("GEN", 1, 1);
        await _syncService.Received().SyncPreferencesAsync(Arg.Any<PreferencesSnapshot>());
    }

    // ═══════════════════════════════════════════════════════════════════
    //  5. Multi-type sync then sign out prevents further syncs
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AuthThenSyncMultipleTypes_ThenSignOut_FurtherSyncsFail()
    {
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("token", "user@bible.app"));
        _authService.IsAuthenticated.Returns(true);

        using var coordinator = CreateCoordinator();
        await coordinator.AuthenticateAsync();

        // Sync all three types
        Assert.True((await coordinator.SyncReadingProgressAsync("PSA", 23, 1)).IsSuccess);
        Assert.True((await coordinator.SyncAnnotationAsync(
            new AnnotationBundle { BookCode = "PSA", Chapter = 23, Verse = 1 })).IsSuccess);
        Assert.True((await coordinator.SyncPreferencesAsync(
            new PreferencesSnapshot { Theme = "Light" })).IsSuccess);

        // Sign out
        coordinator.SignOut();

        // After sign out, auth service reports not authenticated
        _authService.IsAuthenticated.Returns(false);

        // Further syncs still succeed locally and remain queued for later.
        var result = await coordinator.SyncReadingProgressAsync("PSA", 23, 2);
        Assert.True(result.IsSuccess);
        Assert.Equal(4, await _queueManager.GetPendingCountAsync());
        await _syncService.DidNotReceive().SyncReadingProgressAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
    }

    // ═══════════════════════════════════════════════════════════════════
    //  6. Resume flow: offline queue persists across coordinator instances
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResumeFlow_QueuePersistedAcrossRestarts_DrainedOnResume()
    {
        // ── Session 1: user goes offline, queues a sync ────────────────
        _networkMonitor.IsConnected.Returns(false);
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("token", "user@bible.app"));

        using (var session1 = CreateCoordinator())
        {
            await session1.AuthenticateAsync();
            await session1.SyncReadingProgressAsync("MAT", 5, 3);
            await session1.SyncAnnotationAsync(new AnnotationBundle
            {
                BookCode = "MAT", Chapter = 5, Verse = 3,
                Notes = "Blessed are the poor in spirit"
            });
        }
        // session1 disposed — simulates app exit

        // Queue should persist (file-based)
        Assert.Equal(2, await _queueManager.GetPendingCountAsync());

        // ── Session 2: app resumes, online, auth cached ────────────────
        _networkMonitor.IsConnected.Returns(true);
        _authService.IsAuthenticated.Returns(true);

        _syncService.SyncReadingProgressAsync("MAT", 5, 3)
            .Returns(SyncResult.Success(1));
        _syncService.SyncAnnotationAsync(Arg.Any<AnnotationBundle>())
            .Returns(SyncResult.Success(1));

        using var session2 = CreateCoordinator();
        await session2.AuthenticateAsync();

        // Drain the persisted queue
        session2.ForceSync();
        await Task.Delay(1000);

        await _syncService.Received().SyncReadingProgressAsync("MAT", 5, 3);
        await _syncService.Received().SyncAnnotationAsync(
            Arg.Is<AnnotationBundle>(a => a.BookCode == "MAT" && a.Chapter == 5));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  7. SyncProgress events fire during the journey
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncProgressEvents_RaisedByCoordinator_WhenServiceRaises()
    {
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("token", "user@bible.app"));
        _authService.IsAuthenticated.Returns(true);

        _syncService.SyncReadingProgressAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(SyncResult.Success(1));

        using var coordinator = CreateCoordinator();
        await coordinator.AuthenticateAsync();

        var receivedEvents = new List<SyncProgressEventArgs>();
        coordinator.SyncProgress += (_, e) => receivedEvents.Add(e);

        // Simulate the sync service raising a progress event
        _syncService.SyncProgress += Raise.Event<SyncProgressHandler>(
            new SyncStatusInfo
            {
                IsSyncing = true,
                StatusMessage = "Uploading reading progress...",
                ProgressPercentage = 50
            });

        Assert.Single(receivedEvents);
        Assert.True(receivedEvents[0].IsSyncing);
        Assert.Equal("Uploading reading progress...", receivedEvents[0].Message);
        Assert.Equal(50, receivedEvents[0].Progress);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  8. Auth journey end-to-end with local storage verification
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullJourney_ReadingProgress_PreferencesWithCustomSettings_PersistLocally()
    {
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("token", "user@bible.app"));
        _authService.IsAuthenticated.Returns(true);

        _syncService.SyncReadingProgressAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(SyncResult.Success(1));
        _syncService.SyncPreferencesAsync(Arg.Any<PreferencesSnapshot>())
            .Returns(SyncResult.Success(1));

        using var coordinator = CreateCoordinator();
        await coordinator.AuthenticateAsync();

        // Sync reading progress
        await coordinator.SyncReadingProgressAsync("REV", 21, 4);

        // Sync preferences with custom settings (simulates open tabs)
        var prefs = new PreferencesSnapshot
        {
            Theme = "Dark",
            FontSize = 20,
            Language = "en-US",
            CustomSettings =
            {
                ["open_tabs_v1"] = JsonSerializer.Serialize(new[]
                {
                    new { BookCode = "REV", Chapter = 21, Verse = 4 },
                    new { BookCode = "JHN", Chapter = 3, Verse = 16 }
                }),
                ["active_tab_index"] = "0"
            }
        };
        await coordinator.SyncPreferencesAsync(prefs);

        // Verify local storage has the full data
        var storedProgress = await _localStorage.GetObjectAsync<ReadingProgressSnapshot>("CurrentReadingProgress");
        Assert.Equal("REV", storedProgress!.BookCode);
        Assert.Equal(21, storedProgress.Chapter);

        var storedPrefs = await _localStorage.GetObjectAsync<PreferencesSnapshot>("UserPreferences");
        Assert.Equal("Dark", storedPrefs!.Theme);
        Assert.Equal(2, storedPrefs.CustomSettings.Count);
        Assert.Contains("REV", storedPrefs.CustomSettings["open_tabs_v1"]);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  9. Network restored triggers automatic queue drain
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NetworkRestored_AutomaticallySyncsQueuedItems()
    {
        _networkMonitor.IsConnected.Returns(false);
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("token", "user@bible.app"));
        _authService.IsAuthenticated.Returns(true);

        _syncService.SyncAllAsync().Returns(SyncResult.Success(1));

        using var coordinator = CreateCoordinator();
        await coordinator.AuthenticateAsync();

        // Queue an item while offline
        await coordinator.SyncReadingProgressAsync("EXO", 14, 21);
        Assert.Equal(1, await _queueManager.GetPendingCountAsync());

        // Simulate network coming back online
        _networkMonitor.IsConnected.Returns(true);
        _connectivityHandler?.Invoke(true);

        // Wait for automatic drain (500ms stabilization delay + processing)
        await Task.Delay(1500);

        // The coordinator calls SyncAllAsync on the service when connectivity restores
        await _syncService.Received().SyncAllAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  10. Not-authenticated gate blocks every sync type
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NotAuthenticated_AllSyncOperationsQueueLocallyWithoutRemoteCalls()
    {
        _networkMonitor.IsConnected.Returns(true);
        _authService.IsAuthenticated.Returns(false);

        // Don't authenticate — just create coordinator
        using var coordinator = CreateCoordinator();

        var r1 = await coordinator.SyncReadingProgressAsync("GEN", 1, 1);
        Assert.True(r1.IsSuccess);

        var r2 = await coordinator.SyncAnnotationAsync(new AnnotationBundle { BookCode = "GEN" });
        Assert.True(r2.IsSuccess);

        var r3 = await coordinator.SyncPreferencesAsync(new PreferencesSnapshot());
        Assert.True(r3.IsSuccess);

        Assert.Equal(3, await _queueManager.GetPendingCountAsync());

        // Drive was never called
        await _syncService.DidNotReceive().SyncReadingProgressAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
        await _syncService.DidNotReceive().SyncAnnotationAsync(Arg.Any<AnnotationBundle>());
        await _syncService.DidNotReceive().SyncPreferencesAsync(Arg.Any<PreferencesSnapshot>());
    }
}

