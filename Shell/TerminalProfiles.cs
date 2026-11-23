using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Terminal;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private void BuildTerminalProfiles(MenuItem parent, string? folder)
        {
            parent.Items.Clear();
            string defaultId = Services.ThemeManager.GetSetting("DefaultTerminalProfile") ?? "pwsh";
            foreach (var entry in TerminalProfileStore.Load())
            {
                if (entry.Hidden) continue;
                var row = new MenuItem { Header = entry.Name, IsCheckable = true, IsChecked = entry.Id == defaultId,
                    InputGestureText = entry.Id == defaultId ? "F8" : entry.Id == "cmd" ? "Shift+F8" : string.Empty };
                row.Click += (_, _) => OpenShell(entry.Resolve(), entry.StartFolder.Length > 0 ? null : folder);
                parent.Items.Add(row);
            }
            parent.Items.Add(new Separator());
            var manage = new MenuItem();
            manage.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Term_ManageProfiles");
            manage.Click += (_, _) => Dispatcher.BeginInvoke(new System.Action(() => ManageTerminalProfiles_Click(this, new RoutedEventArgs())));
            parent.Items.Add(manage);
        }

        private void LaunchProfiles_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menu) BuildTerminalProfiles(menu, _active.LaunchProfile?.BrowsePath(_active.CurrentFolder) ?? _active.CurrentFolder);
        }

        private void ManageTerminalProfiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TerminalProfilesDialog { Owner = this };
            dialog.ShowDialog();
        }

        /// <summary>
        /// The terminal strip's profile card: a tile per shown profile with its shortcut (F8 opens the default,
        /// Shift+F8 Command Prompt), and a last tile that opens the profile manager.
        /// </summary>
        internal ContextMenu TerminalProfilesCard()
        {
            string? folder = _active.LaunchProfile?.BrowsePath(_active.CurrentFolder) ?? _active.CurrentFolder;
            string defaultId = Services.ThemeManager.GetSetting("DefaultTerminalProfile") ?? "pwsh";
            var card = Controls.CardMenus.TileCard();
            card.Tag = "terminalprofiles";
            Controls.CardMenus.WatchKeyboardStart(card);
            var tiles = TerminalProfileStore.Load().Where(entry => !entry.Hidden).Select(entry =>
                new KillerFamily.Controls.CardTile("", entry.Name,
                    entry.Id == defaultId ? "F8" : entry.Id == "cmd" ? "Shift+F8" : null,
                    () => OpenShell(entry.Resolve(), entry.StartFolder.Length > 0 ? null : folder)) { Id = entry.Id }).ToList();
            tiles.Add(new KillerFamily.Controls.CardTile("", Loc("Str_Term_ManageProfiles"), null,
                () => Dispatcher.BeginInvoke(new System.Action(() => ManageTerminalProfiles_Click(this, new RoutedEventArgs()))))
                { Id = "manage", Shown = Loc("Str_Term_Profiles") });
            card.Items.Add(Controls.CardMenus.Tiles(card, tiles));
            return card;
        }

        internal void TermProfiles_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement button) OpenUnder(TerminalProfilesCard(), button);
        }
    }
}
