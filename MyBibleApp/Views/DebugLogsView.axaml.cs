using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class DebugLogsView : UserControl
{
    public DebugLogsView()
    {
        InitializeComponent();
        AttachLogAutoscroll();
    }

    private void AttachLogAutoscroll()
    {
        if (this.FindControl<ListBox>("LogsListBox") is not { } logsListBox)
            return;

        logsListBox.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name == nameof(ListBox.ItemsSource))
                ScrollToBottom();
        };
    }

    private void ScrollToBottom()
    {
        if (this.FindControl<ListBox>("LogsListBox") is not { } logsListBox)
            return;

        if (logsListBox.ItemCount == 0)
            return;

        logsListBox.ScrollIntoView(logsListBox.ItemCount - 1);
    }

    private async void OnCopyAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppViewModel vm)
            return;

        var copyBox = this.FindControl<TextBox>("CopyHelperTextBox");
        if (copyBox == null)
            return;

        copyBox.Text = string.Join(Environment.NewLine, vm.SyncDebugLogs);
        copyBox.Focus();
        copyBox.SelectAll();
        copyBox.Copy();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppViewModel vm)
            vm.ClearSyncDebugLogs();
    }
}
