using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;

namespace MyBibleApp.Controls;

/// <summary>
/// A scroll gesture recognizer that only activates for Touch input.
/// During annotation mode this replaces the default ScrollGestureRecognizer so that
/// finger touch still scrolls while pen input is left entirely to the ParagraphInkCanvas.
/// </summary>
internal sealed class TouchOnlyScrollGestureRecognizer : ScrollGestureRecognizer
{
    protected override void PointerPressed(PointerPressedEventArgs e)
    {
        // Only track touch; ignore pen and mouse so they never trigger a scroll gesture.
        if (e.Pointer.Type == PointerType.Touch)
            base.PointerPressed(e);
    }

    protected override void PointerMoved(PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
            base.PointerMoved(e);
    }

    protected override void PointerReleased(PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
            base.PointerReleased(e);
    }
}

