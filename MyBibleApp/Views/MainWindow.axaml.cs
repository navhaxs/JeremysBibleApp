using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Controls;
using MyBibleApp.Services;
using MyBibleApp.Services.Sync;

namespace MyBibleApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;

        SharedSyncRuntime.Instance.SyncCoordinator.SyncProgress += OnSyncProgress;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.IsProgrammatic)
            return;

        var shell = this.FindControl<AppShellView>("Shell")
                    ?? this.Content as AppShellView;

        if (shell == null || !shell.HasAuthenticatedTabs())
        {
            Closing -= OnWindowClosing;
            return;
        }

        e.Cancel = true;

        var overlay = this.FindControl<Panel>("SyncOverlay");
        if (overlay != null)
            overlay.IsVisible = true;

        UpdateSyncOverlay("Saving pending changes to Google Drive...", progress: 0);

        try
        {
            await shell.ShutdownAsync();
        }
        catch
        {
            // Never block exit on sync failure
        }
        finally
        {
            if (overlay != null)
                overlay.IsVisible = false;
        }

        Closing -= OnWindowClosing;
        Close();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        SharedSyncRuntime.Instance.SyncCoordinator.SyncProgress -= OnSyncProgress;
        base.OnClosed(e);
    }

    private void OnSyncProgress(object? sender, SyncProgressEventArgs e)
    {
        Dispatcher.UIThread.Post(() => UpdateSyncOverlay(e.Message, e.Progress));
    }

    private void UpdateSyncOverlay(string message, int progress)
    {
        var statusText = this.FindControl<TextBlock>("SyncOverlayStatus");
        var progressBar = this.FindControl<ProgressBar>("SyncOverlayProgress");

        if (statusText != null)
            statusText.Text = string.IsNullOrWhiteSpace(message) ? "Please wait..." : message;

        if (progressBar != null)
        {
            progressBar.IsIndeterminate = progress <= 0 || progress >= 100;
            progressBar.Value = progress;
        }
    }
}