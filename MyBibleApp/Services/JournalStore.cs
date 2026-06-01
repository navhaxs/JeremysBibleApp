using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public sealed class JournalStore : IJournalStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Tracks journals whose ink stroke save failed and need retry on next save
    private readonly Dictionary<(string JournalId, string ChapterKey), IReadOnlyList<JournalInkStroke>> _pendingRetry = new();

    private static string ChapterKey(string bookCode, int chapter) => $"{bookCode}:{chapter}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public JournalStore(string? storagePath = null)
    {
        var storageDir = storagePath ?? GetDefaultStoragePath();
        if (!Directory.Exists(storageDir))
            Directory.CreateDirectory(storageDir);

        _filePath = Path.Combine(storageDir, "journals.json");
    }

    /// <inheritdoc />
    public async Task<Result<Journal>> CreateJournalAsync(JournalCreateRequest request)
    {
        // Validate name length
        if (string.IsNullOrEmpty(request.Name) || request.Name.Length < 1 || request.Name.Length > 100)
            return Result<Journal>.Failure("Journal name must be between 1 and 100 characters.");

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);

            // Check case-insensitive uniqueness
            if (entries.Any(e => string.Equals(e.Metadata.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
                return Result<Journal>.Failure("A journal with this name already exists.");

            var now = DateTime.UtcNow;
            var journal = new Journal
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.Name,
                TranslationId = request.TranslationId,
                TranslationVersionDate = request.TranslationVersionDate,
                BookCode = request.BookCode,
                StartChapter = request.StartChapter,
                StartVerse = request.StartVerse,
                EndChapter = request.EndChapter,
                EndVerse = request.EndVerse,
                ContentHash = request.ContentHash,
                Layout = request.Layout,
                CreatedAtUtc = now,
                LastModifiedUtc = now
            };

            entries.Add(new JournalEntry { Metadata = journal });
            await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);

            return Result<Journal>.Success(journal);
        }
        catch (Exception ex)
        {
            return Result<Journal>.Failure($"Failed to create journal: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Journal>> GetAllJournalsAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, _) = await LoadEntriesAsync().ConfigureAwait(false);
            return entries
                .Select(e => e.Metadata)
                .OrderByDescending(j => j.CreatedAtUtc)
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Journal?> GetJournalAsync(string journalId)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, _) = await LoadEntriesAsync().ConfigureAwait(false);
            return entries.FirstOrDefault(e => e.Metadata.Id == journalId)?.Metadata;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteJournalAsync(string journalId)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return Result.Failure("Journal not found.");

            entries.Remove(entry);

            // Record tombstone so sync knows this was intentionally deleted
            if (!tombstones.Any(t => t.Id == journalId))
                tombstones.Add(new DeletedJournalTombstone { Id = journalId, DeletedAtUtc = DateTime.UtcNow });

            await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to delete journal: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> RenameJournalAsync(string journalId, string newName)
    {
        // Validate new name length
        if (string.IsNullOrEmpty(newName) || newName.Length < 1 || newName.Length > 100)
            return Result.Failure("Journal name must be between 1 and 100 characters.");

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return Result.Failure("Journal not found.");

            // Check case-insensitive uniqueness (excluding the journal being renamed)
            if (entries.Any(e => e.Metadata.Id != journalId &&
                                 string.Equals(e.Metadata.Name, newName, StringComparison.OrdinalIgnoreCase)))
                return Result.Failure("A journal with this name already exists.");

            // Create updated journal with new name (Journal uses init-only properties)
            var updatedJournal = new Journal
            {
                Id = entry.Metadata.Id,
                Name = newName,
                TranslationId = entry.Metadata.TranslationId,
                TranslationVersionDate = entry.Metadata.TranslationVersionDate,
                BookCode = entry.Metadata.BookCode,
                StartChapter = entry.Metadata.StartChapter,
                StartVerse = entry.Metadata.StartVerse,
                EndChapter = entry.Metadata.EndChapter,
                EndVerse = entry.Metadata.EndVerse,
                ContentHash = entry.Metadata.ContentHash,
                Layout = entry.Metadata.Layout,
                CreatedAtUtc = entry.Metadata.CreatedAtUtc,
                LastModifiedUtc = DateTime.UtcNow
            };

            var index = entries.IndexOf(entry);
            entries[index] = new JournalEntry { Metadata = updatedJournal, InkStrokesByChapter = entry.InkStrokesByChapter };
            await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to rename journal: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> UpdateJournalAsync(Journal journal)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journal.Id);
            if (entry == null)
                return Result.Failure("Journal not found.");

            var index = entries.IndexOf(entry);
            entries[index] = new JournalEntry { Metadata = journal, InkStrokesByChapter = entry.InkStrokesByChapter };
            await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update journal: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> SaveInkStrokesAsync(string journalId, string bookCode, int chapter, IReadOnlyList<JournalInkStroke> strokes)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return Result.Failure("Journal not found.");

            var chapterKey = ChapterKey(bookCode, chapter);
            entry.InkStrokesByChapter[chapterKey] = strokes.ToList();
            entry.Metadata.LastModifiedUtc = DateTime.UtcNow;

            try
            {
                // Apply any pending retries for other (journal, chapter) combos before saving
                foreach (var (retryKey, retryStrokes) in _pendingRetry.ToList())
                {
                    if (retryKey.JournalId == journalId && retryKey.ChapterKey == chapterKey)
                        continue; // current save supersedes this
                    var pendingEntry = entries.FirstOrDefault(e => e.Metadata.Id == retryKey.JournalId);
                    if (pendingEntry != null)
                        pendingEntry.InkStrokesByChapter[retryKey.ChapterKey] = retryStrokes.ToList();
                }

                await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);

                _pendingRetry.Clear();

                return Result.Success();
            }
            catch (Exception ex)
            {
                _pendingRetry[(journalId, chapterKey)] = strokes;
                return Result.Failure($"Failed to save ink strokes: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to save ink strokes: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> SaveAllInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return Result.Failure("Journal not found.");

            entry.InkStrokesByChapter.Clear();
            foreach (var group in strokes.GroupBy(s => ChapterKey(s.BookCode, s.ChapterNumber)))
                entry.InkStrokesByChapter[group.Key] = group.ToList();

            entry.Metadata.LastModifiedUtc = DateTime.UtcNow;
            await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to save all ink strokes: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return Result.Failure($"Journal '{journalId}' not found.");

            var key = ChapterKey(stroke.BookCode, stroke.ChapterNumber);
            if (!entry.InkStrokesByChapter.TryGetValue(key, out var bucket))
                entry.InkStrokesByChapter[key] = bucket = [];
            bucket.Add(stroke);
            entry.Metadata.LastModifiedUtc = DateTime.UtcNow;
            await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId, string bookCode, int chapter)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return Result.Failure($"Journal '{journalId}' not found.");

            var key = ChapterKey(bookCode, chapter);
            if (entry.InkStrokesByChapter.TryGetValue(key, out var bucket))
            {
                var removed = bucket.RemoveAll(s => s.Id == strokeId);
                if (removed > 0)
                {
                    var now = DateTime.UtcNow;
                    if (entry.DeletedInkStrokes.All(t => t.StrokeId != strokeId))
                        entry.DeletedInkStrokes.Add(new InkStrokeTombstone
                        {
                            StrokeId = strokeId,
                            DeletedAtUtc = now
                        });
                    entry.Metadata.LastModifiedUtc = now;
                    await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
                }
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId, string bookCode, int chapter)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var chapterKey = ChapterKey(bookCode, chapter);
            if (_pendingRetry.TryGetValue((journalId, chapterKey), out var pendingStrokes))
                return pendingStrokes;

            var (entries, _) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null) return [];

            return entry.InkStrokesByChapter.TryGetValue(chapterKey, out var list) ? list : [];
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<JournalDataSnapshot> GetSnapshotAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            return new JournalDataSnapshot
            {
                Journals = entries,
                DeletedJournals = tombstones,
                LastModifiedUtc = DateTime.UtcNow
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task MergeRemoteAsync(JournalDataSnapshot remote)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (localEntries, localTombstones) = await LoadEntriesAsync().ConfigureAwait(false);

            // Merge tombstones: union of local and remote, keeping latest DeletedAtUtc per ID
            var mergedTombstones = localTombstones.ToDictionary(t => t.Id);
            foreach (var rt in remote.DeletedJournals)
            {
                if (!mergedTombstones.TryGetValue(rt.Id, out var existing) || rt.DeletedAtUtc > existing.DeletedAtUtc)
                    mergedTombstones[rt.Id] = rt;
            }

            // Build a dictionary of local entries keyed by journal ID for fast lookup
            var localById = localEntries.ToDictionary(e => e.Metadata.Id);

            // Build the merged result
            var merged = new List<JournalEntry>();

            // Process remote journals: last-write-wins for conflicts, add remote-only
            var processedIds = new HashSet<string>();
            foreach (var remoteEntry in remote.Journals)
            {
                var id = remoteEntry.Metadata.Id;
                processedIds.Add(id);

                // Skip if this journal has a tombstone newer than the remote entry
                if (mergedTombstones.TryGetValue(id, out var tombstone) &&
                    tombstone.DeletedAtUtc >= remoteEntry.Metadata.LastModifiedUtc)
                    continue;

                if (localById.TryGetValue(id, out var localEntry))
                {
                    merged.Add(MergeJournalEntries(localEntry, remoteEntry));
                }
                else
                {
                    // Remote-only journal — only add if not tombstoned locally
                    if (!mergedTombstones.ContainsKey(id))
                        merged.Add(remoteEntry);
                }
            }

            // Add local-only journals (not present in remote, not tombstoned)
            foreach (var localEntry in localEntries)
            {
                if (!processedIds.Contains(localEntry.Metadata.Id) &&
                    !mergedTombstones.ContainsKey(localEntry.Metadata.Id))
                {
                    merged.Add(localEntry);
                }
            }

            await SaveEntriesAsync(merged, mergedTombstones.Values.ToList()).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static JournalEntry MergeJournalEntries(JournalEntry local, JournalEntry remote)
    {
        // 1. Union stroke tombstones — keep latest DeletedAtUtc per StrokeId
        var mergedStrokeTombstones = local.DeletedInkStrokes.ToDictionary(t => t.StrokeId);
        foreach (var rt in remote.DeletedInkStrokes)
        {
            if (!mergedStrokeTombstones.TryGetValue(rt.StrokeId, out var existing) || rt.DeletedAtUtc > existing.DeletedAtUtc)
                mergedStrokeTombstones[rt.StrokeId] = rt;
        }
        var tombstonedIds = mergedStrokeTombstones.Keys.ToHashSet();

        // 2. Union live strokes from both entries — dedup by Id, exclude tombstoned
        var mergedStrokes = local.InkStrokesByChapter.Values
            .Concat(remote.InkStrokesByChapter.Values)
            .SelectMany(list => list)
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .Where(s => !tombstonedIds.Contains(s.Id))
            .ToList();

        // 3. Re-bucket by "{BookCode}:{ChapterNumber}"
        var mergedByChapter = mergedStrokes
            .GroupBy(s => $"{s.BookCode}:{s.ChapterNumber}")
            .ToDictionary(g => g.Key, g => g.ToList());

        // 4. Keep newer Metadata
        var winnerMetadata = remote.Metadata.LastModifiedUtc > local.Metadata.LastModifiedUtc
            ? remote.Metadata
            : local.Metadata;

        return new JournalEntry
        {
            Metadata = winnerMetadata,
            InkStrokesByChapter = mergedByChapter,
            DeletedInkStrokes = mergedStrokeTombstones.Values.ToList()
        };
    }

    private async Task<(List<JournalEntry> Entries, List<DeletedJournalTombstone> Tombstones)> LoadEntriesAsync()
    {
        return await Task.Run(async () =>
        {
            if (!File.Exists(_filePath))
                return (new List<JournalEntry>(), new List<DeletedJournalTombstone>());

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                    return (new List<JournalEntry>(), new List<DeletedJournalTombstone>());

                var snapshot = JsonSerializer.Deserialize<JournalDataSnapshot>(json, JsonOptions);
                var entries = snapshot?.Journals ?? new List<JournalEntry>();
                var tombstones = snapshot?.DeletedJournals ?? new List<DeletedJournalTombstone>();

                // One-time migration: re-bucket v1 flat inkStrokes into inkStrokesByChapter
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

                return (entries, tombstones);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading journals: {ex.Message}");
                return (new List<JournalEntry>(), new List<DeletedJournalTombstone>());
            }
        }).ConfigureAwait(false);
    }

    private async Task SaveEntriesAsync(List<JournalEntry> entries, List<DeletedJournalTombstone>? tombstones = null)
    {
        await Task.Run(() =>
        {
            var snapshot = new JournalDataSnapshot
            {
                Journals = entries,
                DeletedJournals = tombstones ?? [],
                LastModifiedUtc = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            WriteAtomically(_filePath, json);
        }).ConfigureAwait(false);
    }

    private static void WriteAtomically(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempFilePath, content);
        File.Move(tempFilePath, filePath, overwrite: true);
    }

    private static string GetDefaultStoragePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MyBibleApp", "LocalStorage");
    }
}
