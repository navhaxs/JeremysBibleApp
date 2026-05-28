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
    private readonly Dictionary<string, IReadOnlyList<JournalInkStroke>> _pendingRetry = new();

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

            entries.Add(new JournalEntry { Metadata = journal, InkStrokes = [] });
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
            entries[index] = new JournalEntry { Metadata = updatedJournal, InkStrokes = entry.InkStrokes };
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
            entries[index] = new JournalEntry { Metadata = journal, InkStrokes = entry.InkStrokes };
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
    public async Task<Result> SaveInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return Result.Failure("Journal not found.");

            // Replace ink strokes entirely
            var index = entries.IndexOf(entry);
            var updatedJournal = new Journal
            {
                Id = entry.Metadata.Id,
                Name = entry.Metadata.Name,
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

            entries[index] = new JournalEntry
            {
                Metadata = updatedJournal,
                InkStrokes = strokes.ToList()
            };

            // Also flush any pending retries for other journals
            foreach (var (pendingJournalId, pendingStrokes) in _pendingRetry.ToList())
            {
                if (pendingJournalId == journalId)
                    continue; // Current save supersedes pending retry for same journal

                var pendingEntry = entries.FirstOrDefault(e => e.Metadata.Id == pendingJournalId);
                if (pendingEntry != null)
                {
                    var pendingIndex = entries.IndexOf(pendingEntry);
                    entries[pendingIndex] = new JournalEntry
                    {
                        Metadata = pendingEntry.Metadata,
                        InkStrokes = pendingStrokes.ToList()
                    };
                }
            }

            try
            {
                await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
                // Persistence succeeded — clear all pending retries that were flushed
                _pendingRetry.Remove(journalId);
                foreach (var key in _pendingRetry.Keys.ToList())
                {
                    if (entries.Any(e => e.Metadata.Id == key))
                        _pendingRetry.Remove(key);
                }
                return Result.Success();
            }
            catch (Exception ex)
            {
                // Persistence failed — retain strokes in memory for retry on next save
                _pendingRetry[journalId] = strokes;
                return Result.Failure($"Failed to persist ink strokes (retained in memory for retry): {ex.Message}");
            }
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

            entry.InkStrokes.Add(stroke);
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
    public async Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var (entries, tombstones) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return Result.Failure($"Journal '{journalId}' not found.");

            var removed = entry.InkStrokes.RemoveAll(s => s.Id == strokeId);
            if (removed > 0)
            {
                entry.Metadata.LastModifiedUtc = DateTime.UtcNow;
                await SaveEntriesAsync(entries, tombstones).ConfigureAwait(false);
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
    public async Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            // If there are pending retry strokes in memory for this journal, return those
            // (they represent the most recent state that hasn't been persisted yet)
            if (_pendingRetry.TryGetValue(journalId, out var pendingStrokes))
                return pendingStrokes;

            var (entries, _) = await LoadEntriesAsync().ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.Metadata.Id == journalId);
            if (entry == null)
                return [];

            return entry.InkStrokes;
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
                    // Both local and remote have this journal — keep the one with later LastModifiedUtc
                    merged.Add(remoteEntry.Metadata.LastModifiedUtc > localEntry.Metadata.LastModifiedUtc
                        ? remoteEntry
                        : localEntry);
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

    private async Task<(List<JournalEntry> Entries, List<DeletedJournalTombstone> Tombstones)> LoadEntriesAsync()
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(_filePath))
                return (new List<JournalEntry>(), new List<DeletedJournalTombstone>());

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                    return (new List<JournalEntry>(), new List<DeletedJournalTombstone>());

                var snapshot = JsonSerializer.Deserialize<JournalDataSnapshot>(json, JsonOptions);
                return (snapshot?.Journals ?? new List<JournalEntry>(),
                        snapshot?.DeletedJournals ?? new List<DeletedJournalTombstone>());
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
