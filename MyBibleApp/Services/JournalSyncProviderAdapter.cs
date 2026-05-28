using System.Text.Json;
using System.Threading.Tasks;
using MyBibleApp.Models;
using MyBibleApp.Services.Sync;

namespace MyBibleApp.Services;

/// <summary>
/// Adapts the IJournalStore to the IJournalSyncProvider interface used by SyncCoordinator.
/// Handles JSON serialization/deserialization between the two layers.
/// </summary>
internal sealed class JournalSyncProviderAdapter : IJournalSyncProvider
{
    private readonly IJournalStore _journalStore;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public JournalSyncProviderAdapter(IJournalStore journalStore)
    {
        _journalStore = journalStore;
    }

    public async Task<string> GetSnapshotJsonAsync()
    {
        var snapshot = await _journalStore.GetSnapshotAsync().ConfigureAwait(false);
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public async Task MergeRemoteJsonAsync(string remoteJson)
    {
        var remote = JsonSerializer.Deserialize<JournalDataSnapshot>(remoteJson, JsonOptions);
        if (remote != null)
        {
            await _journalStore.MergeRemoteAsync(remote).ConfigureAwait(false);
        }
    }
}
