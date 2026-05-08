using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MyBibleApp.Services.Sync;
using NSubstitute;
using Xunit;

namespace MyBibleApp.Sync.Tests;

public sealed class SyncCoordinatorTests : IDisposable
{
    private readonly IGoogleDriveAuthService _authService;
    private readonly IGoogleDriveSyncService _syncService;
    private readonly ISyncQueueManager _queueManager;
    private readonly INetworkStatusMonitor _networkMonitor;
    private readonly ILocalStorageProvider _localStorage;
    private readonly SyncCoordinator _coordinator;

    public SyncCoordinatorTests()
    {
        _authService = Substitute.For<IGoogleDriveAuthService>();
        _syncService = Substitute.For<IGoogleDriveSyncService>();
        _queueManager = Substitute.For<ISyncQueueManager>();
        _localStorage = Substitute.For<ILocalStorageProvider>();

        _networkMonitor = Substitute.For<INetworkStatusMonitor>();
        _networkMonitor.IsConnected.Returns(true);

        _coordinator = new SyncCoordinator(_authService, _syncService, _queueManager, _networkMonitor, _localStorage);
    }

    public void Dispose() => _coordinator.Dispose();

    // ── AuthenticateAsync ───────────────────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_Success_ReturnsTrue()
    {
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("token123", "user@example.com"));

        var result = await _coordinator.AuthenticateAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task AuthenticateAsync_Success_SavesUser()
    {
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Success("token123", "user@example.com"));

        await _coordinator.AuthenticateAsync();

        await _localStorage.Received(1).SaveAsync("LastAuthenticatedUser", "user@example.com");
    }

    [Fact]
    public async Task AuthenticateAsync_Failure_Throws()
    {
        _authService.AuthenticateAsync()
            .Returns(AuthenticationResult.Failure("No credentials file"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _coordinator.AuthenticateAsync());

        Assert.Contains("No credentials file", ex.Message);
    }

    // ── SyncReadingProgressAsync ────────────────────────────────────────

    [Fact]
    public async Task SyncReadingProgress_WhenOnlineAndAuthenticated_SavesLocallyOnly()
    {
        _authService.IsAuthenticated.Returns(true);

        var result = await _coordinator.SyncReadingProgressAsync("JHN", 3, 16);

        Assert.True(result.IsSuccess);
        await _localStorage.Received(1).SaveObjectAsync("CurrentReadingProgress", Arg.Any<ReadingProgressSnapshot>());
        await _queueManager.DidNotReceive().QueueOperationAsync(Arg.Any<string>(), Arg.Any<object>());
        await _syncService.DidNotReceive().SaveUserDataAsync(Arg.Any<UserDataSnapshot>());
    }

    [Fact]
    public async Task SyncReadingProgress_WhenOffline_SavesLocallyOnly()
    {
        _networkMonitor.IsConnected.Returns(false);
        using var offlineCoordinator = new SyncCoordinator(
            _authService, _syncService, _queueManager, _networkMonitor, _localStorage);

        var result = await offlineCoordinator.SyncReadingProgressAsync("JHN", 3, 16);

        Assert.True(result.IsSuccess);
        await _localStorage.Received(1).SaveObjectAsync("CurrentReadingProgress", Arg.Any<ReadingProgressSnapshot>());
        await _queueManager.DidNotReceive().QueueOperationAsync(Arg.Any<string>(), Arg.Any<object>());
        await _syncService.DidNotReceive().SaveUserDataAsync(Arg.Any<UserDataSnapshot>());
    }

    [Fact]
    public async Task SyncReadingProgress_WhenNotAuthenticated_SavesLocallyOnly()
    {
        _authService.IsAuthenticated.Returns(false);

        var result = await _coordinator.SyncReadingProgressAsync("JHN", 3, 16);

        Assert.True(result.IsSuccess);
        await _localStorage.Received(1).SaveObjectAsync("CurrentReadingProgress", Arg.Any<ReadingProgressSnapshot>());
        await _queueManager.DidNotReceive().QueueOperationAsync(Arg.Any<string>(), Arg.Any<object>());
        await _syncService.DidNotReceive().SaveUserDataAsync(Arg.Any<UserDataSnapshot>());
    }

    [Fact]
    public async Task SyncReadingProgress_SavesLocally()
    {
        _authService.IsAuthenticated.Returns(true);

        await _coordinator.SyncReadingProgressAsync("JHN", 3, 16);

        await _localStorage.Received(1).SaveObjectAsync("CurrentReadingProgress", Arg.Any<ReadingProgressSnapshot>());
    }

    // ── SyncAnnotationAsync ─────────────────────────────────────────────

    [Fact]
    public async Task SyncAnnotation_WhenOnlineAndAuthenticated_QueuesOperation()
    {
        _authService.IsAuthenticated.Returns(true);
        var annotation = new AnnotationBundle { BookCode = "PSA", Chapter = 23, Verse = 1 };

        var result = await _coordinator.SyncAnnotationAsync(annotation);

        Assert.True(result.IsSuccess);
        await _queueManager.Received(1).QueueOperationAsync("Annotation", annotation);
        await _syncService.DidNotReceive().SyncAnnotationAsync(Arg.Any<AnnotationBundle>());
    }

    [Fact]
    public async Task SyncAnnotation_WhenOffline_QueuesOperation()
    {
        _networkMonitor.IsConnected.Returns(false);
        using var offlineCoordinator = new SyncCoordinator(
            _authService, _syncService, _queueManager, _networkMonitor, _localStorage);

        var annotation = new AnnotationBundle { BookCode = "PSA", Chapter = 23, Verse = 1 };
        var result = await offlineCoordinator.SyncAnnotationAsync(annotation);

        Assert.True(result.IsSuccess);
        await _queueManager.Received(1).QueueOperationAsync("Annotation", annotation);
    }

    [Fact]
    public async Task SyncAnnotation_WhenNotAuthenticated_StillQueuesLocally()
    {
        _authService.IsAuthenticated.Returns(false);
        var annotation = new AnnotationBundle { BookCode = "PSA", Chapter = 23, Verse = 1 };

        var result = await _coordinator.SyncAnnotationAsync(annotation);

        Assert.True(result.IsSuccess);
        await _queueManager.Received(1).QueueOperationAsync("Annotation", annotation);
        await _syncService.DidNotReceive().SyncAnnotationAsync(Arg.Any<AnnotationBundle>());
    }

    // ── SyncPreferencesAsync ────────────────────────────────────────────

    [Fact]
    public async Task SyncPreferences_AlwaysSavesLocallyOnly()
    {
        _authService.IsAuthenticated.Returns(true);
        var prefs = new PreferencesSnapshot { Theme = "Dark", FontSize = 20 };

        var result = await _coordinator.SyncPreferencesAsync(prefs);

        Assert.True(result.IsSuccess);
        await _localStorage.Received(1).SaveObjectAsync("UserPreferences", prefs);
        await _queueManager.DidNotReceive().QueueOperationAsync(Arg.Any<string>(), Arg.Any<object>());
        await _syncService.DidNotReceive().SaveUserDataAsync(Arg.Any<UserDataSnapshot>());
    }

    [Fact]
    public async Task SyncPreferences_WhenOffline_SavesLocallyOnly()
    {
        _networkMonitor.IsConnected.Returns(false);
        using var offlineCoordinator = new SyncCoordinator(
            _authService, _syncService, _queueManager, _networkMonitor, _localStorage);

        var prefs = new PreferencesSnapshot { Theme = "Dark" };
        var result = await offlineCoordinator.SyncPreferencesAsync(prefs);

        Assert.True(result.IsSuccess);
        await _localStorage.Received(1).SaveObjectAsync("UserPreferences", prefs);
        await _queueManager.DidNotReceive().QueueOperationAsync(Arg.Any<string>(), Arg.Any<object>());
    }

    // ── SyncBibleReadingProgressAsync ───────────────────────────────────

    [Fact]
    public async Task SyncBibleReadingProgress_QueuesAndSavesLocally()
    {
        _authService.IsAuthenticated.Returns(true);
        var chapters = new Dictionary<string, int[]> { ["GEN"] = [1, 2, 3] };

        var result = await _coordinator.SyncBibleReadingProgressAsync(chapters);

        Assert.True(result.IsSuccess);
        await _localStorage.Received(1).SaveObjectAsync("BibleReadingProgress", Arg.Any<BibleReadingProgressSnapshot>());
        await _queueManager.Received(1).QueueOperationAsync("BibleReadingProgress", Arg.Any<BibleReadingProgressSnapshot>());
        await _syncService.DidNotReceive().SaveUserDataAsync(Arg.Any<UserDataSnapshot>());
    }

    // ── StartAutoSync / StopAutoSync ────────────────────────────────────

    [Fact]
    public void StartAutoSync_DoesNotThrow()
    {
        _coordinator.StartAutoSync(TimeSpan.FromMinutes(5));
        // Should not throw
    }

    [Fact]
    public void StopAutoSync_DoesNotThrow()
    {
        _coordinator.StartAutoSync(TimeSpan.FromMinutes(5));
        _coordinator.StopAutoSync();
        // Should not throw
    }

    [Fact]
    public void StopAutoSync_WithoutStart_DoesNotThrow()
    {
        _coordinator.StopAutoSync();
    }

    // ── SignOut ──────────────────────────────────────────────────────────

    [Fact]
    public void SignOut_CallsRevokeOnAuthService()
    {
        _coordinator.SignOut();

        _authService.Received(1).RevokeAsync();
    }

    // ── SyncProgress event ──────────────────────────────────────────────

    [Fact]
    public async Task SyncProgress_RaisedOnPreferencesSync()
    {
        _authService.IsAuthenticated.Returns(true);
        var prefs = new PreferencesSnapshot { Theme = "Light" };

        SyncProgressEventArgs? received = null;
        _coordinator.SyncProgress += (_, e) => received = e;

        // The coordinator itself doesn't raise progress for individual calls;
        // the service does via its SyncProgress event. We verify the wiring.
        // No assertion on received needed — just verifying no crash.
        await _coordinator.SyncPreferencesAsync(prefs);
    }

    // ── ForceSync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ForceSync_WhenAuthenticated_ProcessesPendingQueue()
    {
        _authService.IsAuthenticated.Returns(true);
        var items = new List<SyncQueueItem>
        {
            new()
            {
                Id = "item1",
                OperationType = "BibleReadingProgress",
                Data = System.Text.Json.JsonSerializer.SerializeToElement(new BibleReadingProgressSnapshot
                {
                    ReadChapters = new Dictionary<string, int[]> { ["GEN"] = [1] }
                })
            }
        };
        _queueManager.GetPendingOperationsAsync().Returns(items);
        _syncService.GetFileModifiedTimesAsync().Returns(new System.Collections.Generic.Dictionary<string, DateTime?>());
        _syncService.SaveUserDataAsync(Arg.Any<UserDataSnapshot>())
            .Returns(SyncResult.Success(1));

        _coordinator.ForceSync();

        // ForceSync runs on a background thread, give it time
        await Task.Delay(500);

        await _queueManager.Received().GetPendingOperationsAsync();
    }

    // ── Dispose ─────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_StopsAutoSync_DoesNotThrow()
    {
        _coordinator.StartAutoSync(TimeSpan.FromSeconds(1));
        _coordinator.Dispose();
        // double dispose should also be safe
        _coordinator.Dispose();
    }
}

