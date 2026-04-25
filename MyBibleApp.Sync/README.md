# MyBibleApp.Sync

Reusable sync library for Google Drive app-data synchronization.

## What this project owns

- Sync contracts and coordinator (`ISyncCoordinator`, `IGoogleDriveSyncService`, etc.)
- Google Drive sync implementation (`GoogleDriveSyncService`)
- Auth services (`DesktopGoogleDriveAuthService`, `AndroidGoogleDriveAuthService`, `iOSGoogleDriveAuthService`)
- Offline queue and local storage implementations
- Sync model types (`ReadingProgressSnapshot`, `PreferencesSnapshot`, `AnnotationBundle`, `SyncEntity`)

## Why it exists

`MyBibleApp` references this project so sync logic can be tested and evolved independently of the UI app.

## Run tests

```powershell
dotnet test MyBibleApp.Sync.Tests/MyBibleApp.Sync.Tests.csproj
```

