using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MyBibleApp.Services.Sync;
using Xunit;

namespace MyBibleApp.Sync.Tests;

public sealed class FileSyncQueueManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"queue_test_{Guid.NewGuid():N}");
    private readonly FileSyncQueueManager _queue;

    public FileSyncQueueManagerTests()
    {
        Directory.CreateDirectory(_tempDir);
        _queue = new FileSyncQueueManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task QueueOperation_AddsSingleItem()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });

        var pending = await _queue.GetPendingOperationsAsync();
        Assert.Single(pending);
        Assert.Equal("ReadingProgress", pending[0].OperationType);
        Assert.False(pending[0].IsSynced);
    }

    [Fact]
    public async Task QueueOperation_MultipleItems_AllPending()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });
        await _queue.QueueOperationAsync("Annotation", new { BookCode = "EXO" });
        await _queue.QueueOperationAsync("Preferences", new { Theme = "Dark" });

        var pending = await _queue.GetPendingOperationsAsync();
        Assert.Equal(3, pending.Count);
    }

    [Fact]
    public async Task GetPendingCount_ReturnsCorrectCount()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });
        await _queue.QueueOperationAsync("Annotation", new { BookCode = "EXO" });

        var count = await _queue.GetPendingCountAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetPendingCount_EmptyQueue_ReturnsZero()
    {
        var count = await _queue.GetPendingCountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task MarkAsSynced_ExcludesItemFromPending()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });
        await _queue.QueueOperationAsync("Annotation", new { BookCode = "EXO" });

        var all = await _queue.GetPendingOperationsAsync();
        await _queue.MarkAsSyncedAsync(all[0].Id);

        var pending = await _queue.GetPendingOperationsAsync();
        Assert.Single(pending);
        Assert.Equal("Annotation", pending[0].OperationType);
    }

    [Fact]
    public async Task MarkAsSynced_NonExistentId_DoesNotThrow()
    {
        await _queue.MarkAsSyncedAsync("nonexistent-id");
        var count = await _queue.GetPendingCountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RemoveOperation_RemovesFromQueue()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });
        var pending = await _queue.GetPendingOperationsAsync();

        await _queue.RemoveOperationAsync(pending[0].Id);

        var afterRemove = await _queue.GetPendingOperationsAsync();
        Assert.Empty(afterRemove);
    }

    [Fact]
    public async Task ClearQueue_RemovesAll()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });
        await _queue.QueueOperationAsync("Annotation", new { BookCode = "EXO" });

        await _queue.ClearQueueAsync();

        var count = await _queue.GetPendingCountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task QueueItem_DataIsSerializedJson()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "JHN", Chapter = 3, Verse = 16 });

        var pending = await _queue.GetPendingOperationsAsync();
        Assert.Contains("JHN", pending[0].Data);
        Assert.Contains("16", pending[0].Data);
    }

    [Fact]
    public async Task QueueItem_HasUniqueId()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "EXO" });

        var pending = await _queue.GetPendingOperationsAsync();
        Assert.NotEqual(pending[0].Id, pending[1].Id);
    }

    [Fact]
    public async Task QueueItem_HasTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });
        var after = DateTime.UtcNow.AddSeconds(1);

        var pending = await _queue.GetPendingOperationsAsync();
        Assert.InRange(pending[0].QueuedAt, before, after);
    }

    [Fact]
    public async Task QueueOperation_ConcurrentInstancesSharingFile_DoNotCollide()
    {
        var first = new FileSyncQueueManager(_tempDir);
        var second = new FileSyncQueueManager(_tempDir);

        await Task.WhenAll(
            Enumerable.Range(0, 25).Select(index =>
                (index % 2 == 0 ? first : second).QueueOperationAsync("ReadingProgress", new { Index = index })));

        var pending = await first.GetPendingOperationsAsync();
        Assert.Equal(25, pending.Count);
    }

    [Fact]
    public async Task QueueOperation_MultiplePendingPreferences_CompactsToLatestSnapshot()
    {
        await _queue.QueueOperationAsync("Preferences", new { Theme = "Light", FontSize = 16 });
        await _queue.QueueOperationAsync("Preferences", new { Theme = "Dark", FontSize = 18 });

        var pending = await _queue.GetPendingOperationsAsync();

        Assert.Single(pending);
        Assert.Equal("Preferences", pending[0].OperationType);
        Assert.Contains("Dark", pending[0].Data);
        Assert.DoesNotContain("Light", pending[0].Data);
    }

    [Fact]
    public async Task QueueOperation_PreferencesCompaction_DoesNotRemoveOtherOperationTypes()
    {
        await _queue.QueueOperationAsync("ReadingProgress", new { BookCode = "GEN" });
        await _queue.QueueOperationAsync("Preferences", new { Theme = "Light" });
        await _queue.QueueOperationAsync("Preferences", new { Theme = "Dark" });

        var pending = await _queue.GetPendingOperationsAsync();

        Assert.Equal(2, pending.Count);
        Assert.Equal(1, pending.Count(item => item.OperationType == "ReadingProgress"));
        Assert.Equal(1, pending.Count(item => item.OperationType == "Preferences"));
    }
}

