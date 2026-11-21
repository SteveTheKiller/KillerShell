using KillerShell.Controls;
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
            if (!menu.IsOpen) FillSystemToolsCard(menu, admin: false);
            ToggleRailFlyout(menu, SystemToolsRailBtn);
        }

        /// <summary>
        /// The System Tools cards. The click card is the three everyday tools over the two that need
        /// elevation; the right-click card is the elevated launches, with the same two below. E7EF is the
        /// admin shield, as the rows it replaces used it.
        /// </summary>
        internal void FillSystemToolsCard(ContextMenu menu, bool admin)
        {
            var card = CardMenus.Prepare(menu);
            CardMenus.WatchKeyboardStart(card);
            var elevated = new[]
            {
                CardMenus.Tile("registry", 0xE7EF, "Str_TabTitle_RegistryEditor", null, "Ctrl+F11", () => RegistryEditorRail_Click(this, new RoutedEventArgs())),
                CardMenus.Tile("eventviewer", 0xE7EF, "Str_TabTitle_EventViewer", null, "Ctrl+F12", () => EventViewerRail_Click(this, new RoutedEventArgs())),
            };
            var tools = admin
                ? new[]
                {
                    // The shield already says elevated, so the tile shows the tool's short name; the full
                    // caption stays in the tooltip and the accessible name.
                    CardMenus.Tile("processesadmin", 0xE7EF, "Str_Rail_ProcessesAdmin", "Str_Rail_ProcessesOpen", "Ctrl+F9", () => RailProcessesAdmin_Click(this, new RoutedEventArgs())),
                    CardMenus.Tile("storageadmin", 0xE7EF, "Str_Rail_StorageAdmin", "Str_Rail_StorageOpen", "Ctrl+F4", () => RailStorageAdmin_Click(this, new RoutedEventArgs())),
                }
                : new[]
                {
                    CardMenus.Tile("processes", 0xE8FD, "Str_Rail_ProcessesOpen", null, "F9", () => RailProcessesOpen_Click(this, new RoutedEventArgs())),
                    CardMenus.Tile("performance", 0xE9D9, "Str_TabTitle_Performance", null, "F11", () => PerformanceRail_Click(this, new RoutedEventArgs())),
                    CardMenus.Tile("storage", 0xEDA2, "Str_Rail_StorageOpen", null, "F4", () => RailStorageOpen_Click(this, new RoutedEventArgs())),
                };
            card.Items.Add(CardMenus.Tiles(card, tools, elevated));
        }

        private void RailMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
            if (sender == LangButton) { LangButton_Click(sender, e); return; }
            if (sender == ThemeButton) { ThemeButton_Click(sender, e); return; }
            if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
            FlyoutPlacement.UsePane(PaneHost, _treeRight ? SidebarSide.Right : SidebarSide.Left);
            FlyoutPlacement.Attach(menu, button);   // rises with the rail button that opened it
            menu.IsOpen = true;
        }

        private void SystemToolsAdmin_Opening(object sender, ContextMenuEventArgs e)
        {
            ((ContextMenu)SystemToolsRailBtn.Resources["SystemToolsMenu"]).IsOpen = false;
            FillSystemToolsCard(SystemToolsRailBtn.ContextMenu, admin: true);
            RailMenu_Opening(SystemToolsRailBtn, e);
        }
    }
}
