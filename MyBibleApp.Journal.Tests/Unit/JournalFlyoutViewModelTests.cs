using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyBibleApp.Models;
using MyBibleApp.Services;
using MyBibleApp.ViewModels;
using Models = MyBibleApp.Models;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class JournalFlyoutViewModelTests
{
    [Fact]
    public async Task RefreshAsync_PopulatesJournals()
    {
        var store = new FakeJournalStore();
        await store.CreateJournalAsync(TestHelpers.MakeRequest("Alpha"));
        var vm = new JournalFlyoutViewModel(store);

        await vm.RefreshAsync();

        Assert.Single(vm.Journals);
        Assert.Equal("Alpha", vm.Journals[0].Name);
    }

    [Fact]
    public async Task CreateJournalAsync_AddsToJournalsAndFiresActivated()
    {
        var store = new FakeJournalStore();
        var vm = new JournalFlyoutViewModel(store);
        string? activatedId = null;
        vm.JournalActivated += (_, id) => activatedId = id;

        await vm.CreateJournalAsync("Beta", "GEN", 1);

        Assert.Single(vm.Journals);
        Assert.NotNull(activatedId);
    }

    [Fact]
    public async Task DeleteJournalAsync_RemovesFromList_AndFiresDeactivatedIfWasActive()
    {
        var store = new FakeJournalStore();
        var vm = new JournalFlyoutViewModel(store);
        await vm.CreateJournalAsync("Gamma", "GEN", 1);
        var journalId = vm.Journals[0].Id;
        vm.SetActiveJournal(journalId);
        bool deactivated = false;
        vm.JournalDeactivated += (_, _) => deactivated = true;

        await vm.DeleteJournalAsync(journalId);

        Assert.Empty(vm.Journals);
        Assert.True(deactivated);
    }

    [Fact]
    public async Task ActivateJournal_FiresJournalActivatedWithId()
    {
        var store = new FakeJournalStore();
        var vm = new JournalFlyoutViewModel(store);
        await vm.CreateJournalAsync("Delta", "GEN", 1);
        var journalId = vm.Journals[0].Id;
        string? activatedId = null;
        vm.JournalActivated += (_, id) => activatedId = id;

        vm.ActivateJournal(journalId);

        Assert.Equal(journalId, activatedId);
        Assert.Equal(journalId, vm.ActiveJournalId);
    }
}

file sealed class FakeJournalStore : IJournalStore
{
    private readonly List<JournalEntry> _entries = [];

    public Task<Result<Models.Journal>> CreateJournalAsync(JournalCreateRequest request)
    {
        var journal = new Models.Journal
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
            CreatedAtUtc = DateTime.UtcNow,
            LastModifiedUtc = DateTime.UtcNow
        };
        _entries.Add(new JournalEntry { Metadata = journal, InkStrokes = [] });
        return Task.FromResult(Result<Models.Journal>.Success(journal));
    }

    public Task<IReadOnlyList<Models.Journal>> GetAllJournalsAsync() =>
        Task.FromResult<IReadOnlyList<Models.Journal>>(_entries.Select(e => e.Metadata).ToList());

    public Task<Models.Journal?> GetJournalAsync(string journalId) =>
        Task.FromResult(_entries.FirstOrDefault(e => e.Metadata.Id == journalId)?.Metadata);

    public Task<Result> DeleteJournalAsync(string journalId)
    {
        _entries.RemoveAll(e => e.Metadata.Id == journalId);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> RenameJournalAsync(string journalId, string newName) => Task.FromResult(Result.Success());
    public Task<Result> UpdateJournalAsync(Models.Journal journal) => Task.FromResult(Result.Success());
    public Task<Result> SaveInkStrokesAsync(string journalId, IReadOnlyList<JournalInkStroke> strokes) => Task.FromResult(Result.Success());
    public Task<IReadOnlyList<JournalInkStroke>> GetInkStrokesAsync(string journalId) => Task.FromResult<IReadOnlyList<JournalInkStroke>>([]);
    public Task<Result> AppendInkStrokeAsync(string journalId, JournalInkStroke stroke) => Task.FromResult(Result.Success());
    public Task<Result> RemoveInkStrokeAsync(string journalId, string strokeId) => Task.FromResult(Result.Success());
    public Task<JournalDataSnapshot> GetSnapshotAsync() => Task.FromResult(new JournalDataSnapshot { Journals = [], DeletedJournals = [], LastModifiedUtc = DateTime.UtcNow });
    public Task MergeRemoteAsync(JournalDataSnapshot remote) => Task.CompletedTask;
}

file static class TestHelpers
{
    public static JournalCreateRequest MakeRequest(string name) => new()
    {
        Name = name,
        TranslationId = "",
        TranslationVersionDate = "",
        ContentHash = "",
        BookCode = "GEN",
        StartChapter = 1,
        StartVerse = 1,
        EndChapter = 1,
        EndVerse = 31,
        Layout = new JournalLayout
        {
            TextColumnWidthDip = 600,
            LeftMarginDip = 80,
            RightMarginDip = 115,
            FontFamily = "Inter",
            FontSizeDip = 16,
            LineHeightDip = 24
        }
    };
}
