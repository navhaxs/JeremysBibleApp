using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using OpenBibleApp.Models;

namespace OpenBibleApp.Views;

public partial class MainView : UserControl
{
    private readonly Flyout _footnoteFlyout;
    private readonly SelectableTextBlock _footnoteTextBlock;

    public MainView()
    {
        InitializeComponent();

        _footnoteTextBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };

        _footnoteFlyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = new Border
            {
                MaxWidth = 420,
                Padding = new Thickness(8),
                Child = _footnoteTextBlock
            }
        };
    }

    private void OnFootnoteButtonClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not BibleFootnote footnote)
        {
            return;
        }

        _footnoteTextBlock.Text = footnote.Text;
        _footnoteFlyout.ShowAt(button);
    }

    private void OnParagraphListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedIndex != -1)
        {
            listBox.SelectedIndex = -1;
        }
    }
}