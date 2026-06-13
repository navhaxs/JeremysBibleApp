using System.Collections.Generic;
using System.Threading.Tasks;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public interface IJournalStore
{
    /// <summary>Creates a new journal. Returns the created journal or an error.</summary>
    Task<Result<Journal>> CreateJournalAsync(JournalCreateRequest request);

    /// <summary>Gets all journals, ordered by creation date descending.</summary>
    Task<IReadOnlyList<Journal>> GetAllJournalsAsync();

    /// <summary>Gets a single journal by ID.</summary>
    Task<Journal?> GetJournalAsync(string journalId);

    /// <summary>Deletes a journal and all its ink strokes.</summary>
    Task<Result> DeleteJournalAsync(string journalId);

    /// <summary>Renames a journal.</summary>
    Task<Result> RenameJournalAsync(string journalId, string newName);

    /// <summary>Updates a journal's metadata (passage, layout, etc.).</summary>
    Task<Result> UpdateJournalAsync(Journal journal);

    /// <summary>Replaces ink strokes for one chapter of a journal.</summary>
    Task<Result> SaveInkStrokesAsync(string journalId, string bookCode, int chapter, IReadOnlyList<JournalInkStroke> strokes);

    /// <summary>
    /// Replaces all chapter buckets from a flat list. Used when converting ephemeral
    /// strokes to a saved journal — strokes may span multiple chapters.
    /// </summary>
    Task<Result> SaveAllInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes);

    /// <summary>Appends a single ink stroke. Stroke carries its own BookCode + ChapterNumber.</summary>
    Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke);

    /// <summary>Removes a single ink stroke from the specified chapter.</summary>
    Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId, string bookCode, int chapter);

    /// <summary>Loads ink strokes for one chapter of a journal.</summary>
    Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId, string bookCode, int chapter);

    /// <summary>Gets the full journal data snapshot for sync.</summary>
    Task<JournalDataSnapshot> GetSnapshotAsync();

    /// <summary>Merges remote journal data using last-write-wins per journal.</summary>
    Task MergeRemoteAsync(JournalDataSnapshot remote);

    /// <summary>
    /// Called by the sync layer after a successful push to Drive. Clears the in-memory
    /// set of local-only stroke IDs so subsequent removals produce tombstones correctly.
    /// </summary>
    void NotifySyncSucceeded();
}
