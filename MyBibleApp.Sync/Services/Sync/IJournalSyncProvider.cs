using System.Threading.Tasks;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Abstraction for journal data operations needed by the SyncCoordinator.
/// Implemented by the JournalStore in the main app project.
/// </summary>
public interface IJournalSyncProvider
{
    /// <summary>
    /// Gets the full journal data snapshot (metadata + ink strokes) for pushing to the cloud.
    /// </summary>
    Task<string> GetSnapshotJsonAsync();

    /// <summary>
    /// Merges remote journal data (JSON) using last-write-wins per journal.
    /// </summary>
    Task MergeRemoteJsonAsync(string remoteJson);
}
