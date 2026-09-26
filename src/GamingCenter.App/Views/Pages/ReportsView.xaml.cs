using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GamingCenter.App.Views.Pages;

public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    /// <summary>"Export CSV" opens its menu on a normal click, not only on right-click.</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
