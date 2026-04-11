using Avalonia.Controls;
using Avalonia.Input;
using MyBibleApp.ViewModels;

namespace MyBibleApp.Views;

public partial class DebugPointerView : UserControl
{
    private DebugPointerViewModel? _vm;

    public DebugPointerView()
    {
        InitializeComponent();
        _vm = new DebugPointerViewModel();
        DataContext = _vm;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        LogPointerEvent(e, "Pressed");
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        LogPointerEvent(e, "Moved");
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        LogPointerEvent(e, "Released");
    }

    private void LogPointerEvent(PointerEventArgs e, string eventKind)
    {
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        var pos = e.GetPosition(this);

        var propsList = new System.Collections.Generic.List<string>();
        if (props.IsLeftButtonPressed) propsList.Add("LeftBtn");
        if (props.IsRightButtonPressed) propsList.Add("RightBtn");
        if (props.IsMiddleButtonPressed) propsList.Add("MidBtn");
        if (props.IsBarrelButtonPressed) propsList.Add("Barrel");
        if (props.IsEraser) propsList.Add("Eraser");
        if (props.IsInverted) propsList.Add("Inverted");

        var propsText = propsList.Count > 0 ? string.Join(",", propsList) : "(none)";

        _vm?.AddEvent(
            $"{e.Pointer.Type} {eventKind}",
            $"({pos.X:F0},{pos.Y:F0})",
            $"{props.Pressure:F3}",
            $"({props.XTilt:F2},{props.YTilt:F2})",
            $"{props.Twist:F1}",
            propsText
        );
    }

    private void OnClearClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        _vm?.Clear();
    }
}

