using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenBibleApp.Controls;

// Keeps normal touch interactions for scrolling; long-press opens selectable text.
public class LongPressSelectableTextBlock : TextBlock
{
    private const double MoveCancelThreshold = 8;

    private readonly DispatcherTimer _longPressTimer;
    private readonly Flyout _selectionFlyout;
    private readonly SelectableTextBlock _selectionTextBlock;
    private Point _pressPoint;
    private bool _isPressed;

    public static readonly StyledProperty<TimeSpan> LongPressDurationProperty =
        AvaloniaProperty.Register<LongPressSelectableTextBlock, TimeSpan>(nameof(LongPressDuration), TimeSpan.FromMilliseconds(450));

    public LongPressSelectableTextBlock()
    {
        _longPressTimer = new DispatcherTimer();
        _longPressTimer.Tick += OnLongPressTimerTick;

        _selectionTextBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _selectionFlyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = new Border
            {
                MaxWidth = 520,
                MaxHeight = 320,
                Padding = new Thickness(10),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _selectionTextBlock
                }
            }
        };

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    public TimeSpan LongPressDuration
    {
        get => GetValue(LongPressDurationProperty);
        set => SetValue(LongPressDurationProperty, value);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isPressed = true;
        _pressPoint = e.GetPosition(this);

        _longPressTimer.Stop();
        _longPressTimer.Interval = LongPressDuration;
        _longPressTimer.Start();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - _pressPoint.X) > MoveCancelThreshold || Math.Abs(currentPoint.Y - _pressPoint.Y) > MoveCancelThreshold)
        {
            CancelLongPress();
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CancelLongPress();
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CancelLongPress();
    }

    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        CancelLongPress();
        ShowSelectionFlyout();
    }

    private void CancelLongPress()
    {
        _isPressed = false;
        _longPressTimer.Stop();
    }

    private void ShowSelectionFlyout()
    {
        _selectionTextBlock.Text = Text;
        _selectionTextBlock.FontSize = FontSize;
        _selectionTextBlock.LineHeight = LineHeight;
        _selectionTextBlock.Foreground = Foreground;

        _selectionFlyout.ShowAt(this);
    }
}


