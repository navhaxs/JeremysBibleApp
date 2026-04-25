# AGENTS Guide

## Scope and entry points
- Solution: `MyBibleApp.sln` (core app + Desktop/Android/iOS/Browser hosts + sync library + tests + demo).
- Runtime composition starts in `MyBibleApp/App.axaml.cs` and creates `MainViewModel` directly (no DI container).
- Desktop bootstrap is `MyBibleApp.Desktop/Program.cs`; use this as the fastest local iteration target.

## Architecture that matters
- UI and reading logic live in `MyBibleApp` (`Views/`, `ViewModels/`, `Services/`, `Models/`).
- Sync implementation is a separate library: `MyBibleApp.Sync` (referenced by `MyBibleApp/MyBibleApp.csproj`).
- Important: `MyBibleApp.Sync/MyBibleApp.Sync.csproj` compiles only `Services/Sync/*.cs`; files in `MyBibleApp.Sync/Services/*.cs` are currently not compiled.
- Main data flow for sync: `MainViewModel` -> `ISyncCoordinator` -> `IGoogleDriveSyncService` + local queue/storage.
- Progress flow: `IGoogleDriveSyncService.SyncProgress` -> `SyncCoordinator.SyncProgress` -> `MainViewModel` properties/logs -> `SyncControlView`/`SyncDebugView`.

## Sync behavior and persistence
- `SyncCoordinator` is local-first: reading progress and preferences are saved locally before remote sync (`CurrentReadingProgress`, `UserPreferences`).
- Offline behavior queues operations via `ISyncQueueManager`; reconnect or manual `ForceSync()` replays queue.
- Local storage defaults to `%APPDATA%\MyBibleApp\LocalStorage` (`FileBasedLocalStorageProvider`).
- Google Drive storage uses `appDataFolder` JSON files (`user_data.json`, `annotations.json`). `user_data.json` contains both reading progress and preferences in a single file.
- Desktop auth uses OAuth with `DriveAppdata` scope and a token cache folder `token_cache_appdata`.

## Bible content pipeline
- Book metadata and defaults come from assets: `Assets/books.json`, `Assets/last_verse.json`.
- Initial content path: `MainViewModel` loads `Assets/usx/sample-jhn1.usx` via `UsxBibleAssetLoader`.
- On lookup change, `UsxBibleApiLoader` fetches USX from `https://v1.fetch.bible/bibles/eng_bsb/usx/{book}.usx` and caches by book code.
- `UsxBibleParser` normalizes USX into `BibleParagraph` + `BibleFootnote` models (including superscript verse markers).

## Project-specific coding patterns
- Avalonia compiled bindings are expected (`x:DataType` in XAML, `<AvaloniaUseCompiledBindingsByDefault>true`).
- `MainViewModel` is large and stateful by design; many UI actions route through it (auth, sync, lookup, tab persistence).
- `AppShellView` owns multi-tab lifecycle: creates multiple `MainViewModel` instances and persists tab state through preferences keys (`open_tabs_v1`, `active_tab_index`).
- Async/UI rule used in this repo: service calls typically use `ConfigureAwait(false)`; UI updates marshal through `Dispatcher.UIThread`.

## Commands and workflows (verified where noted)
- Build desktop target: `dotnet build MyBibleApp.Desktop/MyBibleApp.Desktop.csproj` (verified).
- Run desktop app: `dotnet run --project MyBibleApp.Desktop/MyBibleApp.Desktop.csproj`.
- Run sync tests: `dotnet test MyBibleApp.Sync.Tests/MyBibleApp.Sync.Tests.csproj` (verified: 84 passing).
- SDK baseline is .NET 10 preview-style (`global.json` pins `10.0.0` with prerelease allowed).
- Central package versions are in `Directory.Packages.props`; update versions there, not per project.

## Integration notes and gotchas
- Credentials are loaded from `MyBibleApp/Assets/credentials.json` in app code (`AssetLoader.Open(...)`), not from solution root.
- There is extensive historical documentation in root markdown files; treat code as source of truth when docs conflict.
- Current desktop build shows NU1903 warning on transitive `Tmds.DBus.Protocol`; keep this in mind for dependency updates.

