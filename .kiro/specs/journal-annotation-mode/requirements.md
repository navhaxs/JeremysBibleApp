# Requirements Document

## Introduction

The Journal Annotation Mode is a dedicated reading and note-taking mode within the Bible app. Users create named "journals" that present Bible text at a fixed width with generous left and right margins, providing empty space for freehand pen annotations. Because the text layout is fixed (width, translation, and content hash are locked at journal creation time), pen strokes remain spatially stable across devices and window resizes. Journal ink data is persisted and synced to the cloud alongside other user data.

## Glossary

- **Journal**: A named, user-created annotation workspace that locks Bible text to a fixed layout width and translation version, providing margin space for pen annotations.
- **Journal_Layout**: The fixed rendering configuration of a Journal, including text column width, left margin width, right margin width, Bible translation identifier, and a content hash.
- **Content_Hash**: A deterministic hash of the Bible text content at the time of Journal creation, used to verify that the underlying text has not changed between devices or over time.
- **Journal_Ink_Stroke**: A single pen stroke recorded within a Journal, stored in layout-relative coordinates so it remains positionally stable regardless of window size.
- **Journal_Store**: The persistence layer responsible for saving and loading Journal metadata and associated ink strokes, both locally and via cloud sync.
- **Journal_Mode_View**: The UI mode that displays Bible text in the fixed Journal layout with annotation margins and accepts pen input.
- **Sync_Coordinator**: The existing service responsible for coordinating local-first persistence and Google Drive cloud synchronization.
- **App_Shell**: The top-level view (AppShellView) that manages application tabs and navigation.

## Requirements

### Requirement 1: Create a Journal

**User Story:** As a Bible reader, I want to create a new journal for a specific Bible passage and translation, so that I have a stable workspace for pen annotations.

#### Acceptance Criteria

1. WHEN the user initiates journal creation and provides a name between 1 and 100 characters (inclusive), THE Journal_Store SHALL create a new Journal with a unique identifier, the user-provided name, the current Bible translation identifier, the selected passage reference (book, chapter, and verse range), and the current date.
2. WHEN a Journal is created, THE Journal_Layout SHALL record a fixed text column width, a left margin width, and a right margin width in device-independent units that together define the rendering geometry.
3. WHEN a Journal is created, THE Journal_Store SHALL compute and store a Content_Hash of the full Bible text content for the selected passage reference (all verses in the specified book, chapter, and verse range) and translation at the time of creation.
4. WHEN a Journal is created, THE Journal_Store SHALL record the Bible translation version date at the time of creation alongside the Content_Hash.
5. IF a journal with the same name (case-insensitive comparison) already exists for the current user, THEN THE Journal_Store SHALL reject the creation and display an error message indicating that the name is already in use.
6. IF the user provides a name that is empty or exceeds 100 characters, THEN THE Journal_Store SHALL reject the creation and display an error message indicating the name length constraint.
7. IF the Bible text for the selected passage and translation is unavailable at creation time, THEN THE Journal_Store SHALL reject the creation and display an error message indicating that the passage content could not be retrieved.

### Requirement 2: Open and Display a Journal

**User Story:** As a Bible reader, I want to open an existing journal and see the Bible text rendered at the fixed journal layout, so that my annotations align with the text exactly as when I created them.

#### Acceptance Criteria

1. WHEN the user opens a Journal, THE Journal_Mode_View SHALL render the Bible text using the Journal_Layout fixed text column width, left margin, and right margin.
2. WHEN the user opens a Journal, THE Journal_Mode_View SHALL load the Bible text using the translation identifier stored in the Journal.
3. WHILE a Journal is open, THE Journal_Mode_View SHALL maintain the fixed text column width regardless of window resize or device screen size.
4. WHEN a Journal is opened, THE Journal_Mode_View SHALL verify the Content_Hash of the current Bible text against the stored Content_Hash before rendering the journal content.
5. IF the Content_Hash verification fails, THEN THE Journal_Mode_View SHALL display a warning to the user indicating that the underlying text has changed since journal creation and SHALL allow the user to continue viewing and annotating the journal after acknowledging the warning.
6. IF the Bible text for the stored translation identifier cannot be loaded within 10 seconds or the translation is unavailable, THEN THE Journal_Mode_View SHALL display an error message indicating the translation could not be loaded and SHALL not render partial or fallback text.

### Requirement 3: Pen Annotation in Journal Mode

**User Story:** As a Bible reader, I want to draw pen annotations in the journal margins and over the text, so that I can take freehand notes alongside Scripture.

#### Acceptance Criteria

1. WHILE a Journal is open, THE Journal_Mode_View SHALL accept pen-type pointer input and render ink strokes with no more than 50 milliseconds of visible latency from input to screen across the full journal area including left margin, text column, and right margin.
2. WHILE a Journal is open, THE Journal_Mode_View SHALL ignore touch and mouse pointer input for inking so that touch scrolling and text selection remain unaffected.
3. WHEN a pen stroke is completed in Journal mode, THE Journal_Mode_View SHALL store the Journal_Ink_Stroke with coordinates relative to the Journal_Layout fixed geometry, including single-point strokes (tap without drag).
4. THE Journal_Ink_Stroke SHALL record points in layout-relative coordinates so that strokes render at the same position on any device displaying the same Journal.
5. WHEN a Journal is opened, THE Journal_Mode_View SHALL load and render all previously saved Journal_Ink_Strokes for that Journal.
6. IF loading previously saved Journal_Ink_Strokes fails, THEN THE Journal_Mode_View SHALL display an error indication to the user and allow continued use of the Journal without the failed strokes.
7. WHILE a Journal is open and eraser mode is active, THE Journal_Mode_View SHALL remove an entire Journal_Ink_Stroke when the pen pointer passes within 14 device-independent pixels of any recorded point in that stroke.

### Requirement 4: Journal Ink Persistence

**User Story:** As a Bible reader, I want my journal annotations saved automatically, so that I do not lose my handwritten notes.

#### Acceptance Criteria

1. WHEN a pen stroke is completed or erased in a Journal, THE Journal_Store SHALL persist the change to local storage within 1 second of the stroke completion or erase action.
2. THE Journal_Store SHALL store Journal_Ink_Strokes grouped by their parent Journal identifier.
3. WHEN the app performs a sync cycle, THE Sync_Coordinator SHALL include Journal data (metadata and ink strokes) in the cloud sync payload.
4. WHEN a sync pull retrieves updated Journal data from the cloud, THE Sync_Coordinator SHALL merge the remote Journal_Ink_Strokes with local data using last-write-wins conflict resolution based on a per-Journal last-modified timestamp.
5. THE Journal_Store SHALL serialize Journal_Ink_Stroke point data in a format that preserves coordinate precision to at least two decimal places.
6. IF a local storage write fails when persisting a Journal_Ink_Stroke, THEN THE Journal_Store SHALL retain the stroke data in memory and retry persistence on the next stroke completion or app-initiated save, and SHALL indicate to the user that unsaved changes exist.
7. IF deserialization of remote Journal data fails during a sync pull, THEN THE Sync_Coordinator SHALL preserve the existing local Journal data unchanged and report a sync error through the SyncProgress event.

### Requirement 5: Journal Cloud Sync

**User Story:** As a Bible reader who uses multiple devices, I want my journals and their annotations synced to the cloud, so that I can continue annotating on any device.

#### Acceptance Criteria

1. WHEN the user authenticates with Google Drive, THE Sync_Coordinator SHALL sync all Journal metadata and Journal_Ink_Strokes to the Google Drive appDataFolder by pulling remote Journal data first and then pushing any locally queued Journal changes.
2. THE Sync_Coordinator SHALL store Journal metadata and Journal_Ink_Strokes together in a dedicated sync file (journals.json) within the appDataFolder.
3. WHEN a Journal is created, renamed, deleted, or has ink strokes added or erased locally, THE Sync_Coordinator SHALL enqueue the change for cloud sync using the existing sync queue mechanism.
4. WHEN Journal data is pulled from the cloud, THE Journal_Store SHALL merge remote Journals and their ink strokes with local data using last-write-wins conflict resolution based on a per-Journal last-modified UTC timestamp.
5. IF the network is unavailable during a Journal sync operation, THEN THE Sync_Coordinator SHALL queue the operation for retry when connectivity is restored, retrying up to 5 times before marking the operation as failed.
6. IF a Journal sync push fails due to a transient error after connectivity is available, THEN THE Sync_Coordinator SHALL retain the queued operation and retry on the next sync cycle.

### Requirement 6: Multiple Journals

**User Story:** As a Bible reader, I want to maintain multiple journals, so that I can organize my annotations by study topic or time period.

#### Acceptance Criteria

1. THE Journal_Store SHALL support storing multiple Journals per user, each with independent metadata and ink strokes.
2. WHEN the user requests a list of journals, THE App_Shell SHALL display all available Journals with their names and creation dates, sorted by creation date in descending order (most recent first).
3. WHEN the user selects a Journal from the list, THE App_Shell SHALL open the selected Journal in Journal_Mode_View.
4. WHEN the user requests to delete a Journal, THE App_Shell SHALL prompt the user for confirmation before proceeding with deletion, and upon confirmation THE Journal_Store SHALL remove the Journal metadata and all associated Journal_Ink_Strokes from local and cloud storage.
5. THE Journal_Store SHALL allow Journals to cover overlapping Bible passages such that each Journal's metadata and Journal_Ink_Strokes remain independently stored, retrievable, and unmodified by operations on other Journals covering the same passage.
6. IF a Journal deletion fails after partial completion, THEN THE Journal_Store SHALL retain the Journal in its pre-deletion state and inform the user that the deletion did not complete.

### Requirement 7: Journal Layout Stability

**User Story:** As a Bible reader, I want my pen annotations to appear in the same position relative to the text on every device, so that my notes remain readable and correctly placed.

#### Acceptance Criteria

1. THE Journal_Layout SHALL define a fixed text column width in device-independent pixels (DIPs) that does not change with window or screen dimensions.
2. WHILE a Journal is displayed, THE Journal_Mode_View SHALL render text using the Journal_Layout's stored font family, font size, line height, and text column width so that line-breaking and paragraph spacing are identical regardless of the device viewport width.
3. THE Journal_Layout SHALL store the font family name, font size in DIPs, and line height in DIPs used for text rendering so that text reflows identically across devices.
4. WHEN a Journal is displayed on a device where the viewport is narrower than the sum of the left margin, text column width, and right margin defined in the Journal_Layout, THE Journal_Mode_View SHALL enable horizontal scrolling rather than reflowing text.
5. WHILE the Content_Hash of the current Bible text matches the Journal's stored Content_Hash, THE Journal_Ink_Stroke coordinates SHALL render at the same offset from the top-left corner of the Journal_Layout as when originally drawn, with no visible displacement.
6. IF the font family stored in the Journal_Layout is not available on the current device, THEN THE Journal_Mode_View SHALL display a warning to the user indicating that layout fidelity may be affected and identify the missing font.

### Requirement 8: Journal Ink Stroke Serialization

**User Story:** As a developer, I want journal ink strokes serialized in a well-defined format, so that data is portable and round-trip stable across serialization and deserialization.

#### Acceptance Criteria

1. THE Journal_Store SHALL serialize each Journal_Ink_Stroke as a JSON object containing a stroke identifier, a list of point coordinates, stroke color, and stroke width.
2. THE Journal_Store SHALL deserialize a previously serialized Journal_Ink_Stroke back into an equivalent in-memory representation.
3. FOR ALL valid Journal_Ink_Stroke objects, serializing then deserializing SHALL produce an equivalent object (round-trip property).
4. WHEN a serialized Journal_Ink_Stroke contains unexpected or missing fields, THE Journal_Store SHALL return a descriptive error rather than silently discarding data.
5. THE Journal_Store SHALL serialize point coordinates as numeric values with at least two decimal places of precision.
