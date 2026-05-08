using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MyBibleApp.Services;

namespace MyBibleApp.Views;

public partial class LocalStorageDebugView : UserControl
{
    private static readonly string[] WatchedKeys =
    [
        "LocalTabState",
        "CurrentReadingProgress",
        "UserPreferences",
        "LastAuthenticatedUser",
    ];

    public LocalStorageDebugView()
    {
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        var storage = SharedSyncRuntime.Instance.LocalStorageProvider;

        var storagePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyBibleApp", "LocalStorage");

        if (this.FindControl<TextBlock>("StoragePathText") is { } pathText)
            pathText.Text = storagePath;

        var sb = new StringBuilder();
        foreach (var key in WatchedKeys)
        {
            var raw = await storage.GetAsync(key);
            sb.AppendLine($"── {key} ──");
            if (raw == null)
            {
                sb.AppendLine("(not found)");
            }
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    sb.AppendLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    sb.AppendLine(raw);
                }
            }
            sb.AppendLine();
        }

        if (this.FindControl<TextBox>("ContentTextBox") is { } textBox)
            textBox.Text = sb.ToString();
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnSaveTabsNowClick(object? sender, RoutedEventArgs e)
    {
        var shell = this.FindAncestorOfType<AppShellView>();
        if (shell != null)
            await shell.ForcePersistTabsAsync();
        await RefreshAsync();
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<TextBox>("ContentTextBox") is not { } textBox)
            return;
        textBox.Focus();
        textBox.SelectAll();
        textBox.Copy();
    }
}
