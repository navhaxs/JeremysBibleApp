using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class JournalFlyoutView : UserControl
{
    public event EventHandler? SaveAsRequested;
    public event EventHandler<string>? DeleteRequested;

    private Grid? _renameBar;
    private Separator? _renameSeparator;
    private TextBox? _renameTextBox;
    private string? _pendingRenameJournalId;

    public JournalFlyoutView()
    {
        InitializeComponent();

        var createButton = this.FindControl<Button>("CreateButton");
        if (createButton != null)
            createButton.Click += OnCreateClicked;

        var saveAsButton = this.FindControl<Button>("SaveAsButton");
        if (saveAsButton != null)
            saveAsButton.Click += OnSaveAsClicked;

        var journalItems = this.FindControl<ItemsControl>("JournalItems");
        if (journalItems != null)
            journalItems.AddHandler(Button.ClickEvent, OnJournalItemButtonClicked);

        _renameBar = this.FindControl<Grid>("RenameBar");
        _renameSeparator = this.FindControl<Separator>("RenameSeparator");
        _renameTextBox = this.FindControl<TextBox>("RenameTextBox");

        var renameConfirmButton = this.FindControl<Button>("RenameConfirmButton");
        if (renameConfirmButton != null)
            renameConfirmButton.Click += async (_, _) => await CommitRenameAsync();

        if (_renameTextBox != null)
            _renameTextBox.KeyDown += OnRenameTextBoxKeyDown;
    }

    private async void OnCreateClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not JournalFlyoutViewModel vm) return;
        var name = $"Journal {DateTime.Now:MMM d, h:mm tt}";
        await vm.CreateJournalAsync(name, vm.CurrentBookCode, vm.CurrentChapter);
    }

    private void OnSaveAsClicked(object? sender, RoutedEventArgs e)
    {
        SaveAsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnJournalItemButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button btn) return;
        if (DataContext is not JournalFlyoutViewModel vm) return;
        var journalId = btn.Tag as string;
        if (journalId == null) return;

        if (btn.Name == "ActivateButton")
        {
            if (vm.ActiveJournalId == journalId)
                vm.DeactivateJournal();
            else
                vm.ActivateJournal(journalId);
        }
        else if (btn.Name == "RenameButton")
            BeginRename(journalId, vm);
        else if (btn.Name == "DeleteButton")
            DeleteRequested?.Invoke(this, journalId);
    }

    private void BeginRename(string journalId, JournalFlyoutViewModel vm)
    {
        var journal = vm.Journals.FirstOrDefault(j => j.Id == journalId);
        if (journal == null) return;

        _pendingRenameJournalId = journalId;

        if (_renameTextBox != null)
        {
            _renameTextBox.Text = journal.Name;
            _renameTextBox.SelectAll();
        }

        if (_renameSeparator != null) _renameSeparator.IsVisible = true;
        if (_renameBar != null) _renameBar.IsVisible = true;
        _renameTextBox?.Focus();
    }

    private async void OnRenameTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await CommitRenameAsync();
        else if (e.Key == Key.Escape)
            CancelRename();
    }

    private async Task CommitRenameAsync()
    {
        if (_pendingRenameJournalId == null) return;
        if (DataContext is not JournalFlyoutViewModel vm) return;

        var newName = _renameTextBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(newName))
            await vm.RenameJournalAsync(_pendingRenameJournalId, newName);

        CancelRename();
    }

    private void CancelRename()
    {
        _pendingRenameJournalId = null;
        if (_renameBar != null) _renameBar.IsVisible = false;
        if (_renameSeparator != null) _renameSeparator.IsVisible = false;
    }
}
