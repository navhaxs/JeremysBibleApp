using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class SyncControlView : UserControl
{
    public SyncControlView()
    {
        InitializeComponent();
    }

    private async void OnAuthButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppViewModel viewModel)
        {
            viewModel.AppendSyncDebugLog("[UI] Authenticate button tapped.");
            await viewModel.AuthenticateAsync();
        }
    }

    private void OnSignOutButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppViewModel viewModel)
        {
            viewModel.SignOut();
        }
    }

    private void OnForceSyncButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppViewModel viewModel)
        {
            viewModel.ForceSync();
        }
    }
}

