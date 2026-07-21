# Chapter-Keyed Ink Stroke Storage + Segment-Distance Eraser

**Date:** 2026-05-29  
**Status:** Approved  
**Scope:** `JournalDataSnapshot`, `JournalStore`, `IJournalStore`, `AppShellView`, `InkOverlayCanvas`

---

## Problem

1. **Storage scan on every chapter switch.** `GetInkStrokesAsync` loads all strokes for the entire journal, then `AppShellView` filters client-side with LINQ `Where(s => s.BookCode == book && s.ChapterNumber == ch)`. As a journal accumulates annotations across many chapters, this scan grows unboundedly.

2. **Eraser misses gaps between sampled points.** Fast pen strokes produce sparse point samples. The current eraser tests proximity to individual points only — so erasing across the middle of a lightly-sampled stroke can miss, producing phantom gaps.

---

## Change 1 — Chapter-Keyed Ink Stroke Storage

### Data model (`JournalDataSnapshot.cs`)

Replace the flat `InkStrokes` list on `JournalEntry` with a chapter-keyed dictionary. Retain the old field as a migration shim.

```csharp
public sealed class JournalEntry
{
    public Journal Metadata { get; set; } = new();

    // Primary store. Key = "{BOOKCODE}:{chapter}" e.g. "GEN:1", "ROM:8", "PSA:119"
    public Dictionary<string, List<JournalInkStroke>> InkStrokesByChapter { get; set; } = new();

    // v1 migration shim — populated by JSON deserializer when reading old format.
    // Set to null after migration so it is omitted from all subsequent writes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JournalInkStroke>? InkStrokes { get; set; }
}
```

Properties change from `init` to `set` to allow in-place migration mutation.

### Chapter key format

```csharp
// Static helper on JournalStore
private static string ChapterKey(string bookCode, int chapter) => $"{bookCode}:{chapter}";
```

Examples: `"GEN:1"`, `"ROM:8"`, `"PSA:119"`, `"REV:22"`.

### Migration (`JournalStore.LoadEntriesAsync`)

After JSON deserialization, before returning entries, run a one-time migration pass per journal entry:

```csharp
bool dirty = false;
foreach (var entry in entries)
{
    if (entry.InkStrokes is { Count: > 0 } legacy && entry.InkStrokesByChapter.Count == 0)
    {
        foreach (var s in legacy)
        {
            var key = ChapterKey(s.BookCode, s.ChapterNumber);
            if (!entry.InkStrokesByChapter.TryGetValue(key, out var bucket))
                entry.InkStrokesByChapter[key] = bucket = [];
            bucket.Add(s);
        }
        entry.InkStrokes = null;
        dirty = true;
    }
}
if (dirty)
    await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
```

Strokes with empty `BookCode` get key `":0"` — they remain visible but unanchored to any chapter filter. This is an edge case for strokes drawn before BookCode was populated.

### `IJournalStore` interface changes

```csharp
// Changed: scoped to one chapter — no more full-journal return
Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId, string bookCode, int chapter);

// Changed: targets single chapter bucket
Task<Result> SaveInkStrokesAsync(string journalId, string bookCode, int chapter, IReadOnlyList<JournalInkStroke> strokes);

// Changed: chapter key provided by caller — no cross-chapter scan
Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId, string bookCode, int chapter);

// Unchanged: stroke already carries BookCode + ChapterNumber
Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke);
```

### `JournalStore` implementation changes

**`_pendingRetry`** type changes:

```csharp
// Before:
private readonly Dictionary<string, IReadOnlyList<JournalInkStroke>> _pendingRetry = new();

// After:
private readonly Dictionary<(string JournalId, string ChapterKey), IReadOnlyList<JournalInkStroke>> _pendingRetry = new();
```

**`GetInkStrokesAsync`:**
- Check `_pendingRetry[(journalId, chapterKey)]`
- Load entries → `entry.InkStrokesByChapter.TryGetValue(chapterKey, ...)` — O(1) dict lookup
- Remove LINQ filter

**`AppendInkStrokeAsync`:**
- Derive `key = ChapterKey(stroke.BookCode, stroke.ChapterNumber)`
- `if (!entry.InkStrokesByChapter.TryGetValue(key, out var list)) entry.InkStrokesByChapter[key] = list = [];`
- `list.Add(stroke)`

**`RemoveInkStrokeAsync`:**
- Accept `bookCode, chapter` params
- `entry.InkStrokesByChapter.TryGetValue(ChapterKey(bookCode, chapter), out var list)` → `list.RemoveAll(s => s.Id == strokeId)`

**`SaveInkStrokesAsync`:**
- Accept `bookCode, chapter` params
- Replace `entry.InkStrokesByChapter[ChapterKey(bookCode, chapter)]` with new list
- `_pendingRetry` key becomes `(journalId, chapterKey)` on failure

**`RenameJournalAsync` / `UpdateJournalAsync`:**
- Update `new JournalEntry { Metadata = ..., InkStrokesByChapter = entry.InkStrokesByChapter }` (replace `InkStrokes = entry.InkStrokes`)

**`CreateJournalAsync`:**
- `new JournalEntry { Metadata = journal }` — `InkStrokesByChapter` already defaults to `new()`

### `AppShellView` call sites

**Tab switch / restore (lines ~213, ~914):**

```csharp
// Before:
var allStrokes = await store.GetInkStrokesAsync(journalId);
var passageStrokes = allStrokes
    .Where(s => s.BookCode == bookCode && s.ChapterNumber == chapter)
    .ToList();

// After:
var passageStrokes = (await store.GetInkStrokesAsync(journalId, bookCode, chapter)).ToList();
```

**`OnStrokeRemoved` (line ~1029):**

```csharp
// Before:
await store.RemoveInkStrokeAsync(journalId, strokeId);

// After:
await store.RemoveInkStrokeAsync(journalId, strokeId, vm.BookCode, vm.SelectedLookupChapter);
```

**`OnSaveJournalClicked` / ephemeral-to-journal conversion (line ~974):**

This call site passes a flat `ephemeral` list that may contain strokes from multiple chapters (user may have navigated while in ephemeral mode). The new chapter-scoped `SaveInkStrokesAsync` handles only one bucket.

Add a bulk overload to the interface for this case:

```csharp
// Replaces all chapter buckets from a flat list — used for ephemeral-to-journal promotion only.
Task<Result> SaveAllInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes);
```

Implementation: group strokes by `ChapterKey(s.BookCode, s.ChapterNumber)`, build `InkStrokesByChapter`, replace entire dict in one atomic file write.

Call site changes from:
```csharp
await store.SaveInkStrokesAsync(journal.Id, ephemeral);
```
to:
```csharp
await store.SaveAllInkStrokesAsync(journal.Id, ephemeral);
```

### Sync compatibility

`MergeRemoteAsync` merges at journal granularity (last-write-wins by `LastModifiedUtc`). Both local and remote entries carry `InkStrokesByChapter` after migration. Merge logic is unchanged.

**Cross-device risk:** a device running old app code winning the sync merge will write old `inkStrokes` format. New device loads it, migration auto-re-buckets on first `LoadEntriesAsync`. Old device loading new format sees empty `inkStrokes` — strokes invisible until next push from updated device.

**Mitigation:** update both devices together. No dual-write strategy required.

---

## Change 2 — Segment-Distance Eraser (`InkOverlayCanvas.cs`)

### Problem

Current inner loop in `EraseAt` tests distance to each **point** individually:

```csharp
foreach (var p in s.Points)
{
    var dx = p.X - adjustedPoint.X;
    var dy = p.Y - adjustedPoint.Y;
    if (dx * dx + dy * dy <= radiusSq) { hit = true; break; }
}
```

A stroke sampled at 60 Hz during a fast pen gesture can have 20+ px gaps between consecutive points. Erasing over these gaps produces a miss even though the stroke visually passes through the eraser circle.

### Fix

Replace point-distance test with **segment-distance test** — minimum distance from eraser centre to each line segment formed by consecutive point pairs:

```csharp
private static double DistToSegmentSq(Point p, Point a, Point b)
{
    double dx = b.X - a.X, dy = b.Y - a.Y;
    double lenSq = dx * dx + dy * dy;
    if (lenSq < 1e-10)
    {
        dx = p.X - a.X; dy = p.Y - a.Y;
        return dx * dx + dy * dy;
    }
    double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0.0, 1.0);
    dx = p.X - (a.X + t * dx);
    dy = p.Y - (a.Y + t * dy);
    return dx * dx + dy * dy;
}
```

Inner loop changes from iterating points to iterating segments:

```csharp
// Single point: existing dot check unchanged
if (s.Points.Count == 1)
{
    var dx = s.Points[0].X - adjustedPoint.X;
    var dy = s.Points[0].Y - adjustedPoint.Y;
    hit = dx * dx + dy * dy <= radiusSq;
}
else
{
    for (int j = 0; j < s.Points.Count - 1 && !hit; j++)
        hit = DistToSegmentSq(adjustedPoint, s.Points[j], s.Points[j + 1]) <= radiusSq;
    // Also test last point (segment loop covers j=0..n-2, last point covered as segment endpoint)
}
```

No change to AABB pre-filter, eraser radius, event firing, or undo/redo behaviour. External API unchanged.

**Performance:** ~2× inner-loop cost vs point-only (one extra multiply + clamp per pair). Negligible at chapter-level stroke counts. AABB pre-filter still eliminates non-candidates before the inner loop runs.

---

## Files Changed

| File | Change |
|---|---|
| `MyBibleApp/Models/JournalDataSnapshot.cs` | `JournalEntry`: add `InkStrokesByChapter`, keep `InkStrokes` as migration shim |
| `MyBibleApp/Services/IJournalStore.cs` | Update 3 method signatures, add `SaveAllInkStrokesAsync` |
| `MyBibleApp/Services/JournalStore.cs` | Migration, `_pendingRetry` type, all ink store methods, `RenameJournalAsync`, `UpdateJournalAsync` |
| `MyBibleApp/Views/AppShellView.axaml.cs` | 2 call sites: `GetInkStrokesAsync`, `RemoveInkStrokeAsync` |
| `MyBibleApp/Controls/InkOverlayCanvas.cs` | `EraseAt`: replace point loop with segment loop, add `DistToSegmentSq` |

---

## Tests

- `JournalStore`: old-format JSON migrates correctly to `InkStrokesByChapter`
- `JournalStore`: `GetInkStrokesAsync` returns only requested chapter strokes
- `JournalStore`: `AppendInkStrokeAsync` creates bucket when chapter not yet seen
- `JournalStore`: `RemoveInkStrokeAsync` targets correct chapter bucket
- `JournalStore`: `SaveInkStrokesAsync` replaces single chapter bucket, other chapters unaffected
- `JournalStore`: `_pendingRetry` keyed by `(journalId, chapterKey)` — retry applies to correct chapter
- `JournalStore`: `SaveAllInkStrokesAsync` correctly groups multi-chapter flat list into buckets
- `JournalFlyoutViewModelTests.cs`: update stub implementation of `IJournalStore` for changed signatures
- `InkOverlayCanvas` eraser: segment-distance hit fires on midpoint between sampled points
- `InkOverlayCanvas` eraser: stroke with single point still erases correctly
