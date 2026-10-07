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

        private void RailMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
            if (sender == LangButton) { LangButton_Click(sender, e); return; }
            if (sender == ThemeButton) { ThemeButton_Click(sender, e); return; }
            if (sender is not FrameworkElement { ContextMenu: { } menu }) return;
            FlyoutPlacement.UsePane(PaneHost);
            FlyoutPlacement.Attach(menu, (UIElement)sender);
            menu.IsOpen = true;
        }

        private void SystemToolsAdmin_Opening(object sender, ContextMenuEventArgs e)
        {
            ((ContextMenu)SystemToolsRailBtn.Resources["SystemToolsMenu"]).IsOpen = false;
            RailMenu_Opening(SystemToolsRailBtn, e);
        }
    }
}
