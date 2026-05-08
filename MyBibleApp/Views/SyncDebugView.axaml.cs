using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class SyncDebugView : UserControl
{
    public SyncDebugView()
    {
        InitializeComponent();
        AttachLogAutoscroll();
    }

    private void AttachLogAutoscroll()
    {
        if (this.FindControl<ListBox>("SyncLogsListBox") is not { } logsListBox)
            return;

        logsListBox.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name == nameof(ListBox.ItemsSource))
                ScrollToBottom();
        };
    }

    private void ScrollToBottom()
    {
        if (this.FindControl<ListBox>("SyncLogsListBox") is not { } logsListBox)
            return;

        if (logsListBox.ItemCount == 0)
            return;

        logsListBox.ScrollIntoView(logsListBox.ItemCount - 1);
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppViewModel vm)
            await vm.RefreshSyncDebugDataAsync();
    }

    private void OnClearLogsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppViewModel vm)
            vm.ClearSyncDebugLogs();
    }

    private void OnSyncNowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppViewModel vm)
            vm.ForceSync();
    }

    private async void OnClearRemoteDataClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppViewModel vm)
            await vm.ClearRemoteDataAsync();
    }

    private async void OnCopySnapshotClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<TextBox>("SnapshotTextBox") is not { } snapshotTextBox)
            return;

        snapshotTextBox.Focus();
        snapshotTextBox.SelectAll();
        snapshotTextBox.Copy();

        await Task.CompletedTask;
    }
}





