using System.Collections.Generic;
using System.Threading.Tasks;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

public interface IJournalStore
{
    /// <summary>Creates a new journal. Returns the created journal or an error.</summary>
    Task<Result<Journal>> CreateJournalAsync(JournalCreateRequest request);

    /// <summary>Gets all journals for the current user, ordered by creation date descending.</summary>
    Task<IReadOnlyList<Journal>> GetAllJournalsAsync();

    /// <summary>Gets a single journal by ID.</summary>
    Task<Journal?> GetJournalAsync(string journalId);

    /// <summary>Deletes a journal and all its ink strokes.</summary>
    Task<Result> DeleteJournalAsync(string journalId);

    /// <summary>Renames a journal.</summary>
    Task<Result> RenameJournalAsync(string journalId, string newName);

    /// <summary>Updates a journal's metadata (passage, layout, etc.).</summary>
    Task<Result> UpdateJournalAsync(Journal journal);

    /// <summary>Saves ink strokes for a journal (full replacement).</summary>
    Task<Result> SaveInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes);

    /// <summary>Appends a single ink stroke to the journal without replacing existing strokes.</summary>
    Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke);

    /// <summary>Removes a single ink stroke from the journal by its ID.</summary>
    Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId);

    /// <summary>Loads ink strokes for a journal.</summary>
    Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId);

    /// <summary>Gets the full journal data snapshot for sync.</summary>
    Task<JournalDataSnapshot> GetSnapshotAsync();

    /// <summary>Merges remote journal data using last-write-wins per journal.</summary>
    Task MergeRemoteAsync(JournalDataSnapshot remote);
}
