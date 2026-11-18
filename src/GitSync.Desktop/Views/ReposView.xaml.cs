using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GitSync.Desktop.Views;

public partial class ReposView : UserControl
{
    public ReposView() => InitializeComponent();

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -250;
        menu.IsOpen = true;
    }
}
