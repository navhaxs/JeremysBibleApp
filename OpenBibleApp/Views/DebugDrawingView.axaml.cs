using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OpenBibleApp.Models;

namespace OpenBibleApp.Views;

public partial class DebugDrawingView : UserControl
{
    public DebugDrawingView()
    {
        InitializeComponent();

        var inkCanvas = this.FindControl<Controls.ParagraphInkCanvas>("TestInkCanvas");
        var clearButton = this.FindControl<Button>("ClearButton");
        var statusText = this.FindControl<TextBlock>("StatusText");

        if (inkCanvas != null)
        {
            // Initialize the ink canvas with an empty stroke collection
            inkCanvas.InkStrokes = new ObservableCollection<BibleInkStroke>();

            // Hook pointer events to show status
            inkCanvas.AddHandler(PointerPressedEvent, (s, e) =>
            {
                statusText.Text = $"Pen Pressed: {e.Pointer.Type} at {e.GetPosition(inkCanvas)}";
            }, handledEventsToo: true);

            inkCanvas.AddHandler(PointerMovedEvent, (s, e) =>
            {
                if (e.Pointer.Type == PointerType.Pen)
                    statusText.Text = $"Pen Moved: {e.GetPosition(inkCanvas)} - Strokes: {inkCanvas.InkStrokes?.Count ?? 0}";
            }, handledEventsToo: true);

            inkCanvas.AddHandler(PointerReleasedEvent, (s, e) =>
            {
                if (e.Pointer.Type == PointerType.Pen)
                    statusText.Text = $"Pen Released - Total strokes: {inkCanvas.InkStrokes?.Count ?? 0}";
            }, handledEventsToo: true);
        }

        if (clearButton != null)
        {
            clearButton.Click += (s, e) =>
            {
                if (inkCanvas?.InkStrokes != null)
                {
                    inkCanvas.InkStrokes.Clear();
                    statusText.Text = "Drawing cleared. Ready for new input.";
                }
            };
        }
    }
}

