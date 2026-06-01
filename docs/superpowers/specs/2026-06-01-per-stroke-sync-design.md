# Per-Stroke Sync — Design Spec

**Date:** 2026-06-01
**Status:** Approved

## Problem

Current journal sync uses last-write-wins at the `JournalEntry` level. If two devices draw strokes offline and sync, one device's strokes are silently dropped — whichever entry has the older `LastModifiedUtc` loses entirely.

## Goal

Strokes drawn on any device survive sync. Strokes erased on any device stay erased everywhere. No changes to real-time collaboration (out of scope).

## Approach

Stroke-level 2P-Set CRDT: union of live strokes minus union of tombstones. Mirrors the existing journal-level tombstone pattern one level down.

Strokes are immutable once drawn (add/erase only), so no per-stroke modification timestamps are needed.

---

## Data Model

**File:** `MyBibleApp/Models/JournalDataSnapshot.cs`

Add a new tombstone model:

```csharp
public sealed class InkStrokeTombstone
{
    public string StrokeId { get; init; } = string.Empty;
    public DateTime DeletedAtUtc { get; init; }
}
```

Add a field to `JournalEntry`:

```csharp
public List<InkStrokeTombstone> DeletedInkStrokes { get; set; } = [];
```

No changes to `JournalInkStroke`. Backwards compatible — existing persisted JSON deserializes `DeletedInkStrokes` as empty.

---

## Erase Operation

**File:** `MyBibleApp/Services/JournalStore.cs` — `RemoveInkStrokeAsync`

After removing the stroke from the live chapter list, append a tombstone:

```csharp
entry.DeletedInkStrokes.Add(new InkStrokeTombstone
{
    StrokeId = strokeId,
    DeletedAtUtc = DateTime.UtcNow
});
```

`IJournalStore` interface signature unchanged. No changes to callers.

---

## Merge Logic

**File:** `MyBibleApp/Services/JournalStore.cs` — `MergeRemoteAsync`

Replace the existing last-write-wins branch (for journal entries present on both devices) with per-stroke union:

```
For each journal ID present on BOTH devices:
  1. Merge stroke tombstones:
       union local.DeletedInkStrokes + remote.DeletedInkStrokes
       keep latest DeletedAtUtc per StrokeId
  2. Build tombstoned-ID set from merged tombstones
  3. Merge live strokes:
       union InkStrokesByChapter entries from both devices
       deduplicate by StrokeId (same ID = same stroke, keep either copy)
       exclude any stroke whose Id is in tombstoned-ID set
  4. Re-bucket merged strokes into InkStrokesByChapter
       keyed by "{BookCode}:{ChapterNumber}" (existing convention)
  5. Metadata: keep whichever has later LastModifiedUtc (unchanged)
  6. Produce merged JournalEntry with unified strokes + unified tombstones
```

Remote-only and local-only journal entries: no change to existing behavior.
Journal-level tombstones (deleted journals): no change.

No changes to `SyncCoordinator`, `JournalSyncProviderAdapter`, or `IJournalSyncProvider` — merge is fully self-contained in `JournalStore`.

---

## Tombstone Lifecycle

Tombstones are kept forever (v1). For personal-scale journal usage (occasional ink annotations), tombstone accumulation is negligible — a tombstone is ~50 bytes vs. a stroke which carries a full `Points` list.

### Known Limitations

- **TODO:** Add tombstone pruning if payload size becomes measurable. Safe pruning requires knowing the oldest offline period across all devices (e.g., prune tombstones older than 90 days only if all devices have synced within that window). Implementing this without a device registry risks resurrecting erased strokes on long-offline devices. Defer until profiling justifies it.

---

## Testing

**File:** `MyBibleApp.Journal.Tests/Unit/JournalStoreTests.cs` (or equivalent)

Key scenarios to cover in unit tests:

| Scenario | Expected result |
|---|---|
| Device A adds stroke X, device B adds stroke Y, sync | Both X and Y survive |
| Device A adds stroke X, device B erases stroke X (after prior sync), sync | X is removed; tombstone propagates to A |
| Both devices add stroke with same ID (duplicate) | Only one copy kept |
| Remote-only journal with strokes | Added locally unchanged |
| Local-only journal with strokes | Kept unchanged |
| Tombstone on remote for local stroke | Stroke removed locally |
| Tombstone on local for remote stroke | Stroke excluded when merging |

---

## File Map

| File | Change |
|---|---|
| `MyBibleApp/Models/JournalDataSnapshot.cs` | Add `InkStrokeTombstone` model; add `DeletedInkStrokes` to `JournalEntry` |
| `MyBibleApp/Services/JournalStore.cs` | `RemoveInkStrokeAsync`: write tombstone; `MergeRemoteAsync`: per-stroke union |
| `MyBibleApp.Journal.Tests/Unit/JournalStoreTests.cs` | Add merge and erase tombstone tests |
