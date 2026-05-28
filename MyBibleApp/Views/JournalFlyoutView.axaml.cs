using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class JournalFlyoutView : UserControl
{
    public event EventHandler? SaveAsRequested;

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

    private async void OnJournalItemButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button btn) return;
        if (DataContext is not JournalFlyoutViewModel vm) return;
        var journalId = btn.Tag as string;
        if (journalId == null) return;

        if (btn.Name == "ActivateButton")
            vm.ActivateJournal(journalId);
        else if (btn.Name == "DeleteButton")
            await vm.DeleteJournalAsync(journalId);
    }
}
