using System;
using System.Threading;
using Avalonia.Platform;
using MyBibleApp.Services.Sync;

namespace MyBibleApp.Services;

internal sealed class SharedSyncRuntime
{
    private const int AutoSyncIntervalMinutes = 2;
    private static readonly Lazy<SharedSyncRuntime> SharedInstance =
        new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private SharedSyncRuntime(
        IGoogleDriveAuthService authService,
        IGoogleDriveSyncService syncService,
        ISyncQueueManager queueManager,
        INetworkStatusMonitor networkMonitor,
        ILocalStorageProvider localStorage,
        ISyncCoordinator syncCoordinator)
    {
        GoogleDriveAuthService = authService;
        GoogleDriveSyncService = syncService;
        SyncQueueManager = queueManager;
        NetworkStatusMonitor = networkMonitor;
        LocalStorageProvider = localStorage;
        SyncCoordinator = syncCoordinator;
    }

    public static SharedSyncRuntime Instance => SharedInstance.Value;

    public IGoogleDriveAuthService GoogleDriveAuthService { get; }

    public IGoogleDriveSyncService GoogleDriveSyncService { get; }

    public ISyncQueueManager SyncQueueManager { get; }

    public INetworkStatusMonitor NetworkStatusMonitor { get; }

    public ILocalStorageProvider LocalStorageProvider { get; }

    public ISyncCoordinator SyncCoordinator { get; }

    private static SharedSyncRuntime Create()
    {
        IGoogleDriveAuthService authService = PlatformHelper.IsAndroid
            ? new AndroidGoogleDriveAuthService()
            : new DesktopGoogleDriveAuthService(
                () => AssetLoader.Open(new Uri("avares://MyBibleApp/Assets/credentials.desktop.json")));

        var syncService = new GoogleDriveSyncService(authService);
        var queueManager = new FileSyncQueueManager();
        var networkMonitor = new NetworkStatusMonitor();
        var localStorage = new FileBasedLocalStorageProvider();
        var syncCoordinator = new SyncCoordinator(authService, syncService, queueManager, networkMonitor, localStorage);

        syncCoordinator.StartAutoSync(TimeSpan.FromMinutes(AutoSyncIntervalMinutes));

        return new SharedSyncRuntime(authService, syncService, queueManager, networkMonitor, localStorage, syncCoordinator);
    }
}