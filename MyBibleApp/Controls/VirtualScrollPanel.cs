using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MyBibleApp.Controls;

/// <summary>
/// StackPanel replacement for a windowed ListBox that adds virtual top/bottom
/// padding to represent unloaded content above and below the realized window.
/// Setting TopPadding/BottomPadding keeps the scroll extent equal to the full
/// virtual book height even when only a subset of chapters is loaded.
/// </summary>
public class VirtualScrollPanel : Panel
{
    public static readonly StyledProperty<double> TopPaddingProperty =
        AvaloniaProperty.Register<VirtualScrollPanel, double>(nameof(TopPadding));

    public static readonly StyledProperty<double> BottomPaddingProperty =
        AvaloniaProperty.Register<VirtualScrollPanel, double>(nameof(BottomPadding));

    public double TopPadding
    {
        get => GetValue(TopPaddingProperty);
        set => SetValue(TopPaddingProperty, value);
    }

    public double BottomPadding
    {
        get => GetValue(BottomPaddingProperty);
        set => SetValue(BottomPaddingProperty, value);
    }

    static VirtualScrollPanel()
    {
        TopPaddingProperty.Changed.AddClassHandler<VirtualScrollPanel>(
            (p, _) => p.InvalidateMeasure());
        BottomPaddingProperty.Changed.AddClassHandler<VirtualScrollPanel>(
            (p, _) => p.InvalidateMeasure());
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var childConstraint = new Size(availableSize.Width, double.PositiveInfinity);
        double totalChildHeight = 0;
        double maxWidth = 0;

        foreach (var child in Children)
        {
            child.Measure(childConstraint);
            totalChildHeight += child.DesiredSize.Height;
            if (child.DesiredSize.Width > maxWidth)
                maxWidth = child.DesiredSize.Width;
        }

        return new Size(maxWidth, TopPadding + totalChildHeight + BottomPadding);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = TopPadding;

        foreach (var child in Children)
        {
            var childHeight = child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, finalSize.Width, childHeight));
            y += childHeight;
        }

        return finalSize;
    }
}
