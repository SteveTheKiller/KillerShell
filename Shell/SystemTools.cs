using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private void SystemToolsRail_Click(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)SystemToolsRailBtn.Resources["SystemToolsMenu"];
            SystemToolsRailBtn.ContextMenu.IsOpen = false;
            menu.PlacementTarget = SystemToolsRailBtn;
            menu.Placement = PlacementMode.Right;
            menu.HorizontalOffset = 4;
            menu.IsOpen = true;
        }
    }
}
