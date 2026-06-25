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

## Layout engine versioning (annotation compatibility)

**Any change to bible text rendering layout must increment `JournalLayout.LayoutEngineVersion`.**

Pen annotation strokes are anchored to paragraph Y-positions via `AnchorContentTop`. If the layout engine produces different paragraph positions for the same content, existing strokes will appear shifted or misaligned. This includes changes to:

- Paragraph measurement, spacing, or gap logic
- Font size / line height defaults or rendering
- Column width calculation or text margin handling
- USX-to-paragraph parsing that affects paragraph count or order
- Windowed/virtualised scroll behaviour that shifts paragraph offsets

`LayoutEngineVersion` is an `int` on `JournalLayout` (persisted in journal data files). Increment it whenever any of the above changes. On load, a version mismatch between the stored value and the current engine version is a signal that anchor positions may be stale; display a warning or apply a migration if feasible. Never silently discard strokes.

Current version: **1** (set this explicitly on `JournalLayout` when creating or resaving a journal).

### Migrator architecture (`InkAnchorMigrator`)

Two distinct migration layers run at stroke-load time (`LoadJournalStrokes` + `ReplaceChapterStrokes` in `MainView`):

**Layer 1 — Format migration (`MigrateGlobalAnchors`):** Converts legacy strokes where `AnchorChapter == 0`. Old strokes stored `AnchorParagraphIndex` as a *global* index across all paragraphs in the loaded book. Windowed scrolling broke this — only one chapter's paragraphs are in memory at a time. Migration looks up `allParagraphs[globalIdx]`, resolves to `(chapter, localIndex)` via `_paragraphChapterInfo`, and rewrites the stroke. Strokes with `AnchorChapter > 0` pass through with zero allocation. Runs regardless of layout engine version.

**Layer 2 — Layout engine version migration (version-dispatch block in `Migrate`):** Infrastructure for adjusting `AnchorContentTop` values when the layout engine changes between the version a journal was created under and the current version. Currently a no-op (`CurrentVersion == 1`). When `CurrentVersion` is bumped to N, add a `if (fromVersion < N) result = MigrateV{N-1}ToV{N}(result, ...)` step here.

**Key distinction:** Layer 1 is a one-time storage schema fix. Layer 2 is a coordinate correction. A stroke can need both: old format *and* created under an older layout engine.

`MainView._activeLayoutEngineVersion` tracks the loaded journal's version; `SetJournalLayout` updates it (normalises 0 → 1) whenever a journal is activated or cleared.

### How to bump the layout engine version

1. Increment `JournalLayout.CurrentVersion` in `MyBibleApp/Models/Journal.cs`.
2. Add a `MigrateV{old}ToV{new}` method to `InkAnchorMigrator` that adjusts `AnchorContentTop` for affected strokes.
3. Wire it into the version-dispatch block in `InkAnchorMigrator.Migrate`.
4. Update `AGENTS.md` "Current version" above.

## Integration notes and gotchas
- Credentials are loaded from `MyBibleApp/Assets/credentials.json` in app code (`AssetLoader.Open(...)`), not from solution root.
- There is extensive historical documentation in root markdown files; treat code as source of truth when docs conflict.
- Current desktop build shows NU1903 warning on transitive `Tmds.DBus.Protocol`; keep this in mind for dependency updates.

