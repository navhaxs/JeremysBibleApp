re a# Implementation Plan: Journal Annotation Mode

## Overview

This plan implements a dedicated journal annotation workspace within the Bible app. Users create named journals that lock Bible text to a fixed-width layout with margins for freehand pen annotations. The implementation builds from data models and services up through ViewModels and UI, integrating with the existing ink infrastructure and sync pipeline.

## Tasks

- [x] 1. Define data models and core interfaces
  - [x] 1.1 Create Journal data models
    - Create `Models/Journal.cs` with `Journal`, `JournalLayout`, `JournalInkStroke`, `StrokePoint`, `JournalDataSnapshot`, `JournalEntry`, `JournalCreateRequest`, `JournalSummary` classes
    - Create `Models/Result.cs` with generic `Result` and `Result<T>` types
    - All models should be serializable with System.Text.Json
    - _Requirements: 1.1, 1.2, 7.1, 7.3, 8.1, 8.5_

  - [x] 1.2 Create IJournalStore and IContentHashService interfaces
    - Create `Services/IJournalStore.cs` with all CRUD, ink stroke, snapshot, and merge methods as defined in the design
    - Create `Services/IContentHashService.cs` with `ComputeHash` and `Verify` methods
    - _Requirements: 1.1, 1.3, 2.4, 3.3, 4.1, 4.2, 5.4_

- [x] 2. Implement ContentHashService
  - [x] 2.1 Implement ContentHashService
    - Create `Services/ContentHashService.cs` implementing `IContentHashService`
    - Use SHA-256 to compute a deterministic hash from `IReadOnlyList<BibleParagraph>` (concatenate verse text in order)
    - Implement `Verify` method that recomputes and compares
    - _Requirements: 1.3, 2.4_

  - [x]* 2.2 Write property test for content hash determinism (Property 4)
    - **Property 4: Content hash determinism and verification**
    - **Validates: Requirements 2.4**

- [x] 3. Implement JournalStore local persistence
  - [x] 3.1 Implement JournalStore with local file persistence
    - Create `Services/JournalStore.cs` implementing `IJournalStore`
    - Use `FileBasedLocalStorageProvider` pattern to persist `journals.json` at `%APPDATA%\MyBibleApp\LocalStorage\journals.json`
    - Implement `CreateJournalAsync` with name validation (1-100 chars, case-insensitive uniqueness), GUID generation, content hash computation, and timestamp
    - Implement `GetAllJournalsAsync` returning journals sorted by `CreatedAtUtc` descending
    - Implement `GetJournalAsync`, `DeleteJournalAsync`, `RenameJournalAsync`
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 6.1, 6.2, 6.4, 6.5_

  - [x] 3.2 Implement ink stroke persistence in JournalStore
    - Implement `SaveInkStrokesAsync` (full replacement per journal)
    - Implement `GetInkStrokesAsync` (load strokes for a journal)
    - Serialize strokes as JSON with point coordinates to at least 2 decimal places
    - Implement retry-on-failure behavior: retain in memory, retry on next save
    - _Requirements: 4.1, 4.2, 4.5, 4.6, 8.1, 8.2, 8.3, 8.5_

  - [x] 3.3 Implement JSON serialization with error handling
    - Configure System.Text.Json serialization options for `JournalInkStroke` and `Journal` types
    - Implement deserialization that returns descriptive errors for missing/invalid fields rather than silently discarding
    - Handle malformed JSON gracefully with specific error messages
    - _Requirements: 8.1, 8.2, 8.3, 8.4_

  - [x]* 3.4 Write property test for journal creation completeness (Property 1)
    - **Property 1: Journal creation produces complete metadata**
    - **Validates: Requirements 1.1, 1.2, 1.3, 1.4**

  - [x]* 3.5 Write property test for duplicate name rejection (Property 2)
    - **Property 2: Duplicate journal name rejection**
    - **Validates: Requirements 1.5**

  - [x]* 3.6 Write property test for invalid name rejection (Property 3)
    - **Property 3: Invalid journal name rejection**
    - **Validates: Requirements 1.6**

  - [x]* 3.7 Write property test for stroke persistence round-trip (Property 6)
    - **Property 6: Stroke persistence round-trip**
    - **Validates: Requirements 3.5**

  - [x]* 3.8 Write property test for stroke isolation by journal (Property 8)
    - **Property 8: Stroke isolation by journal**
    - **Validates: Requirements 4.2, 6.1, 6.5**

  - [x]* 3.9 Write property test for journal list sorting (Property 11)
    - **Property 11: Journal list sorted by creation date descending**
    - **Validates: Requirements 6.2**

  - [x]* 3.10 Write property test for ink stroke serialization round-trip (Property 12)
    - **Property 12: Ink stroke serialization round-trip**
    - **Validates: Requirements 8.1, 8.2, 8.3, 8.5**

  - [x]* 3.11 Write property test for malformed JSON errors (Property 13)
    - **Property 13: Malformed JSON produces descriptive errors**
    - **Validates: Requirements 8.4**

- [x] 4. Checkpoint - Core services verified
  - Ensure all tests pass, ask the user if questions arise.

- [x] 5. Implement sync integration
  - [x] 5.1 Implement JournalStore snapshot and merge methods
    - Implement `GetSnapshotAsync` to produce a `JournalDataSnapshot` of all journals and strokes
    - Implement `MergeRemoteAsync` using last-write-wins per journal based on `LastModifiedUtc`
    - Journals present only in one snapshot are preserved in the merge result
    - _Requirements: 4.4, 5.1, 5.2, 5.4_

  - [x] 5.2 Extend SyncCoordinator for journal operations
    - Add `SyncJournalDataAsync()` method to `SyncCoordinator`
    - Handle `"Journal"` operation type in `ProcessQueuedOperationAsync`
    - Enqueue sync operations on journal create, rename, delete, stroke add, and stroke erase
    - Implement retry logic (up to 5 retries) for network failures
    - _Requirements: 5.1, 5.3, 5.5, 5.6_

  - [x] 5.3 Extend GoogleDriveSyncService for journals.json
    - Add `GetJournalDataAsync()` and `SaveJournalDataAsync()` methods to `GoogleDriveSyncService`
    - Operate on `journals.json` file in Google Drive `appDataFolder`
    - Handle deserialization failures by preserving local data and reporting sync error via `SyncProgress` event
    - _Requirements: 5.1, 5.2, 4.7_

  - [x]* 5.4 Write property test for last-write-wins merge (Property 9)
    - **Property 9: Last-write-wins merge correctness**
    - **Validates: Requirements 4.4, 5.4**

  - [x]* 5.5 Write property test for mutations enqueue sync (Property 10)
    - **Property 10: Journal mutations enqueue sync operations**
    - **Validates: Requirements 5.3**

- [x] 6. Checkpoint - Sync integration verified
  - Ensure all tests pass, ask the user if questions arise.

- [x] 7. Implement JournalModeViewModel
  - [x] 7.1 Create JournalModeViewModel
    - Create `ViewModels/JournalModeViewModel.cs` extending `ViewModelBase`
    - Implement `OpenJournalAsync` to load journal metadata, Bible text via existing USX pipeline, and ink strokes
    - Implement content hash verification on open, setting `ShowContentHashWarning` when mismatch detected
    - Implement 10-second timeout for Bible text loading with error state
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 3.5_

  - [x] 7.2 Implement ink stroke management in JournalModeViewModel
    - Implement `SaveStrokeAsync` to persist new strokes and enqueue sync
    - Implement `EraseStrokeAsync` to remove a stroke by ID and enqueue sync
    - Implement `UndoLastStrokeAsync` to remove the most recently added stroke
    - Update `LastModifiedUtc` on the journal after each mutation
    - Persist within 1 second of stroke completion/erase
    - _Requirements: 3.3, 3.7, 4.1, 4.3, 5.3_

- [x] 8. Implement JournalListViewModel
  - [x] 8.1 Create JournalListViewModel
    - Create `ViewModels/JournalListViewModel.cs` extending `ViewModelBase`
    - Implement `CreateJournalAsync` with name, translation, passage parameters and layout defaults
    - Implement `DeleteJournalAsync` (ViewModel handles confirmation state, delegates to store)
    - Implement `RenameJournalAsync` and `RefreshAsync`
    - Expose `ObservableCollection<JournalSummary> Journals` for binding
    - _Requirements: 1.1, 6.1, 6.2, 6.3, 6.4, 6.5, 6.6_

- [x] 9. Implement JournalInkCanvas control
  - [x] 9.1 Create JournalInkCanvas custom control
    - Create `Controls/JournalInkCanvas.cs` as a custom Avalonia `Control`
    - Accept only pen-type pointer input (ignore touch and mouse for inking)
    - Store points in layout-relative coordinates (origin = top-left of journal layout area)
    - Support pen, highlighter (multiply blend), and eraser modes
    - Render strokes using Skia with the same approach as existing `InkOverlayCanvas`
    - Implement eraser hit detection: remove entire stroke when pen passes within 14 DIPs of any recorded point
    - Target ≤50ms visible latency from input to screen
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.7, 7.5_

  - [x]* 9.2 Write property test for eraser hit detection (Property 7)
    - **Property 7: Eraser hit detection correctness**
    - **Validates: Requirements 3.7**

  - [x]* 9.3 Write property test for layout-relative coordinate storage (Property 5)
    - **Property 5: Layout-relative coordinate storage**
    - **Validates: Requirements 3.3, 3.4**

- [x] 10. Implement Journal UI views
  - [x] 10.1 Create JournalListView
    - Create `Views/JournalListView.axaml` and code-behind
    - Display list of journals with name and creation date, sorted by creation date descending
    - Provide create, rename, and delete actions with confirmation dialog for delete
    - Use compiled bindings with `x:DataType="vm:JournalListViewModel"`
    - _Requirements: 6.2, 6.3, 6.4_

  - [x] 10.2 Create JournalModeView
    - Create `Views/JournalModeView.axaml` and code-behind
    - Render Bible text at fixed column width with left and right margins from `JournalLayout`
    - Enable horizontal scrolling when viewport is narrower than total layout width
    - Display content hash warning when `ShowContentHashWarning` is true
    - Display missing font warning when stored font family is unavailable
    - Integrate `JournalInkCanvas` overlay for pen annotations
    - Use compiled bindings with `x:DataType="vm:JournalModeViewModel"`
    - _Requirements: 2.1, 2.2, 2.3, 2.5, 2.6, 7.1, 7.2, 7.3, 7.4, 7.6_

- [x] 11. Wire navigation and integration
  - [x] 11.1 Integrate journal navigation into AppShellView
    - Add navigation path from `AppShellView` to `JournalListView`
    - Add navigation from journal list to `JournalModeView` when a journal is selected
    - Expose `IJournalStore` instance via `SharedSyncRuntime` for ViewModel access
    - Register journal-related services in app startup (`App.axaml.cs`)
    - _Requirements: 6.3_

- [x] 12. Checkpoint - Create test project and run all tests
  - [x] 12.1 Create MyBibleApp.Journal.Tests project
    - Create `MyBibleApp.Journal.Tests/MyBibleApp.Journal.Tests.csproj` targeting net10.0
    - Add package references: `xunit`, `FsCheck.Xunit`, `Microsoft.NET.Test.Sdk`
    - Add project reference to `MyBibleApp`
    - Add versions to `Directory.Packages.props`
    - Create `Generators/JournalGenerators.cs` and `Generators/InkStrokeGenerators.cs` with FsCheck Arbitrary instances for Journal, JournalInkStroke, JournalCreateRequest, and related types
    - _Requirements: All (test infrastructure)_

- [x] 13. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- The test project uses FsCheck.Xunit for property-based testing as specified in the design
- All services follow the existing pattern: no DI container, instances created directly and exposed via SharedSyncRuntime
- Sync integration follows the existing SyncCoordinator pattern with queue-based operations

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2"] },
    { "id": 1, "tasks": ["2.1", "12.1"] },
    { "id": 2, "tasks": ["2.2", "3.1"] },
    { "id": 3, "tasks": ["3.2", "3.3"] },
    { "id": 4, "tasks": ["3.4", "3.5", "3.6", "3.7", "3.8", "3.9", "3.10", "3.11"] },
    { "id": 5, "tasks": ["5.1"] },
    { "id": 6, "tasks": ["5.2", "5.3"] },
    { "id": 7, "tasks": ["5.4", "5.5"] },
    { "id": 8, "tasks": ["7.1", "8.1"] },
    { "id": 9, "tasks": ["7.2"] },
    { "id": 10, "tasks": ["9.1"] },
    { "id": 11, "tasks": ["9.2", "9.3", "10.1", "10.2"] },
    { "id": 12, "tasks": ["11.1"] }
  ]
}
```
