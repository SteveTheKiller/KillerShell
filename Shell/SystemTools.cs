using System.Windows;
using System.Windows.Controls;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private void SystemToolsRail_Click(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)SystemToolsRailBtn.Resources["SystemToolsMenu"];
            SystemToolsRailBtn.ContextMenu.IsOpen = false;
            ToggleRailFlyout(menu);
        }

        private void SystemToolsAdmin_Opening(object sender, ContextMenuEventArgs e)
        {
            ((ContextMenu)SystemToolsRailBtn.Resources["SystemToolsMenu"]).IsOpen = false;
            FlyoutPlacement.UsePane(PaneHost);
            FlyoutPlacement.Attach(SystemToolsRailBtn.ContextMenu, this);
        }
    }
}
