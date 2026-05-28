using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using MyBibleApp.Models;
using MyBibleApp.Services;
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public sealed class JournalFlyoutViewModel : ViewModelBase
{
    private readonly IJournalStore _journalStore;
    private string? _activeJournalId;
    private bool _hasEphemeralStrokes;

    public JournalFlyoutViewModel(IJournalStore journalStore)
    {
        _journalStore = journalStore;
    }

    public ObservableCollection<Journal> Journals { get; } = [];

    public string? ActiveJournalId
    {
        get => _activeJournalId;
        private set => this.RaiseAndSetIfChanged(ref _activeJournalId, value);
    }

    public bool HasEphemeralStrokes
    {
        get => _hasEphemeralStrokes;
        set => this.RaiseAndSetIfChanged(ref _hasEphemeralStrokes, value);
    }

    public string CurrentBookCode { get; set; } = string.Empty;
    public int CurrentChapter { get; set; } = 1;

    public event EventHandler<string>? JournalActivated;
    public event EventHandler? JournalDeactivated;

    public async Task RefreshAsync()
    {
        var all = await _journalStore.GetAllJournalsAsync();
        Journals.Clear();
        foreach (var j in all)
            Journals.Add(j);
    }

    public async Task CreateJournalAsync(string name, string bookCode, int chapter)
    {
        var request = new JournalCreateRequest
        {
            Name = name,
            TranslationId = "",
            TranslationVersionDate = "",
            ContentHash = "",
            BookCode = bookCode,
            StartChapter = chapter,
            StartVerse = 1,
            EndChapter = chapter,
            EndVerse = 999,
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
        var result = await _journalStore.CreateJournalAsync(request);
        if (!result.IsSuccess) return;

        Journals.Add(result.Value!);
        ActivateJournal(result.Value!.Id);
    }

    public async Task DeleteJournalAsync(string journalId)
    {
        await _journalStore.DeleteJournalAsync(journalId);
        var toRemove = Journals.FirstOrDefault(j => j.Id == journalId);
        if (toRemove != null) Journals.Remove(toRemove);
        if (ActiveJournalId == journalId)
        {
            ActiveJournalId = null;
            JournalDeactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RenameJournalAsync(string journalId, string newName)
    {
        await _journalStore.RenameJournalAsync(journalId, newName);
        await RefreshAsync();
    }

    public void ActivateJournal(string journalId)
    {
        ActiveJournalId = journalId;
        JournalActivated?.Invoke(this, journalId);
    }

    public void DeactivateJournal()
    {
        ActiveJournalId = null;
        JournalDeactivated?.Invoke(this, EventArgs.Empty);
    }

    public void SetActiveJournal(string? journalId)
    {
        ActiveJournalId = journalId;
    }
}
