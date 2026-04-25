using System;
using System.IO;
using System.Threading.Tasks;
using MyBibleApp.Services.Sync;
using Xunit;

namespace MyBibleApp.Sync.Tests;

public sealed class SyncInfrastructureTests
{
    [Fact]
    public async Task QueueManager_RoundTripsPendingItem()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_queue_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var queueManager = new FileSyncQueueManager(tempDir);
            await queueManager.QueueOperationAsync("ReadingProgress", new { BookCode = "JHN", Chapter = 3, Verse = 16 });

            var pending = await queueManager.GetPendingOperationsAsync();

            Assert.Single(pending);
            Assert.Equal("ReadingProgress", pending[0].OperationType);
            Assert.False(pending[0].IsSynced);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LocalStorageProvider_RoundTripsObjectPayload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"local_storage_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var storage = new FileBasedLocalStorageProvider(tempDir);
            var key = "UserPreferences";
            var payload = new { Theme = "Dark", FontSize = 20 };

            await storage.SaveObjectAsync(key, payload);
            var raw = await storage.GetAsync(key);

            Assert.False(string.IsNullOrWhiteSpace(raw));
            Assert.Contains("\"Dark\"", raw, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}

