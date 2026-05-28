# Journal Mode Redesign

**Date:** 2026-05-28
**Status:** Approved

## Summary

Remove the standalone Journal mode view/screen. Integrate journaling directly into the existing app shell. Pen annotations on any tab are either ephemeral (unsaved) or persisted to a named journal. The active journal is selected per-tab. All ink IS the journal.

## Architecture & Data Flow

Each tab record gains two fields:

- `ActiveJournalId: Guid?` — null means ephemeral mode
- `EphemeralInkStrokes: List<JournalInkStroke>` — in-memory buffer, cleared on "save as journal"

When a stroke completes (`InkOverlayCanvas` → `MainView` → `AppShellView`):

```
if (tab.ActiveJournalId != null)
    JournalStore.AppendStroke(journalId, passage, stroke)
    SyncCoordinator.EnqueueJournalSync()
else
    tab.EphemeralInkStrokes.Add(stroke)   // unsaved, in-memory only
```

On tab switch (`SelectTab`):
- Save leaving tab state as normal (ephemeral strokes already in tab record)
- If incoming tab has `ActiveJournalId`: load journal's strokes for current passage from `JournalStore`
- If null: restore `tab.EphemeralInkStrokes`

Journal layout/font settings apply to `MainView` reader when a journal is active (column width, font size, margins). Default reader settings used when no journal active.

## Data Model

### Tab record
Add to `AppShellView` internal tab representation:
```csharp
Guid? ActiveJournalId
List<JournalInkStroke> EphemeralInkStrokes
```

### Journal model
- `Passage` field demoted to `HomePassage: PassageRef?` — creation origin for display only, not a constraint
- `JournalLayout` retained — affects MainView reader when journal active
- Journals accumulate ink across any passage visited while active

### JournalInkStroke (updated)
Coordinate system migrates from layout-relative `StrokePoint(x, y)` to paragraph-anchored format matching `InkOverlayCanvas`:
```csharp
PassageRef Passage       // book/chapter
string ParagraphKey      // anchor to specific paragraph
// x,y points relative to paragraph bounds
```

### JournalDataSnapshot (updated)
Strokes grouped by passage rather than flat list. Sync/serialization structure updated accordingly.

### Data migration
Not required — app is POC. Old journal files with layout-relative coords discarded on load; journal metadata (name, layout, home passage) retained.

## UI/UX

### Toolbar indicator
A journal button in the MainView toolbar (near pen tools):
- No active journal, no strokes: neutral journal icon
- No active journal, ephemeral strokes exist: icon with "Unsaved" badge/dot
- Active journal: truncated journal name

Tapping the button opens the journal flyout.

### Journal flyout
Anchored to the toolbar button:

```
[ + New Journal ]
─────────────────
○ Genesis study           ← tap to activate
● Romans 8 notes          ← currently active
○ Prayer annotations
─────────────────
[ Save as Journal… ]      ← only when ephemeral strokes exist
```

- Tap row: activate that journal
- Long-press or swipe row: delete / rename
- Gear icon on active row: open layout settings panel (column width, font size, margins)

### Switching journals
- While **ephemeral strokes exist**: prompt — "Save strokes as a journal before switching?" — Save / Discard / Cancel
- While **another journal active**: silent switch; strokes already continuously persisted

### "Save as Journal" flow
1. User taps "Save as Journal…" in flyout
2. Name input dialog
3. Journal created, ephemeral strokes moved in, `ActiveJournalId` set, ephemeral buffer cleared

## Sync

No structural change to `SyncCoordinator`. `JournalSyncProviderAdapter` serializes updated `JournalDataSnapshot` format (passage-grouped strokes). Remote merge logic unchanged (last-write-wins per stroke id).

Ephemeral strokes are never synced — intentional.

## Error Handling

| Scenario | Behaviour |
|----------|-----------|
| `JournalStore` write fails | Stroke kept in in-memory list for session; error toast shown; retry on next sync |
| Journal deleted while active on tab | Tab falls back to ephemeral mode; `ActiveJournalId` cleared |
| App crash with unsaved ephemeral strokes | Lost by design; no recovery |

## Testing

**Unit:**
- Stroke routing logic (ephemeral vs journal path)
- `JournalStore.AppendStroke` / `LoadStrokes`
- `JournalDataSnapshot` serialization with passage-grouped format

**Integration:**
- Tab switch preserves correct strokes per tab
- Journal activate / deactivate applies correct layout to reader
- "Save as Journal" flow: ephemeral → named, strokes transferred

## Deleted Code

The following are removed entirely:

- `Views/JournalModeView.axaml` + `.axaml.cs`
- `Views/JournalListView.axaml` + `.axaml.cs`
- `ViewModels/JournalModeViewModel.cs`
- `ViewModels/JournalListViewModel.cs`
- `Controls/JournalInkCanvas.cs`

## New Code

- `Views/JournalFlyoutView.axaml` + `.axaml.cs` — lightweight flyout panel
- `ViewModels/JournalFlyoutViewModel.cs` — journal list, create/delete/rename, save-as logic
