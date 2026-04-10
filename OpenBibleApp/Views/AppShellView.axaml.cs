using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenBibleApp.Views;

public partial class AppShellView : UserControl
{
    public AppShellView()
    {
        InitializeComponent();
    }

    private void OnDebugToggleClick(object? sender, RoutedEventArgs e)
    {
        var mainView = this.FindControl<MainView>("MainView");
        var debugView = this.FindControl<DebugPointerView>("DebugView");
        var debugDrawingView = this.FindControl<DebugDrawingView>("DebugDrawingView");
        var button = (Button?)sender;

        if (mainView is null || debugView is null || debugDrawingView is null || button is null)
            return;

        var showDebug = !debugView.IsVisible;
        mainView.IsVisible = !showDebug;
        debugView.IsVisible = showDebug;
        debugDrawingView.IsVisible = false;
        button.Content = showDebug ? "Show Bible Reader" : "Show Debug Pointer (Dev)";
    }

    private void OnDebugDrawingToggleClick(object? sender, RoutedEventArgs e)
    {
        var mainView = this.FindControl<MainView>("MainView");
        var debugView = this.FindControl<DebugPointerView>("DebugView");
        var debugDrawingView = this.FindControl<DebugDrawingView>("DebugDrawingView");
        var button = (Button?)sender;

        if (mainView is null || debugDrawingView is null || button is null)
            return;

        var showDebugDrawing = !debugDrawingView.IsVisible;
        mainView.IsVisible = !showDebugDrawing;
        debugView.IsVisible = false;
        debugDrawingView.IsVisible = showDebugDrawing;
        button.Content = showDebugDrawing ? "Show Bible Reader" : "Show Debug Drawing (Dev)";
    }
}


