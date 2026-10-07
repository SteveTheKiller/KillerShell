using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        internal void TermProfiles_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement button) return;
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            var holder = new MenuItem();
            BuildTerminalProfiles(holder, _active.LaunchProfile?.BrowsePath(_active.CurrentFolder) ?? _active.CurrentFolder);
            while (holder.Items.Count > 0)
            {
                object item = holder.Items[0];
                holder.Items.RemoveAt(0);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
    }
}
