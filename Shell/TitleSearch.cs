using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private int _titleSearchScope;

        private static string SearchScopeKey(int scope) => scope switch
        {
            1 => "Str_SearchScope_Tabs",
            2 => "Str_SearchScope_Computer",
            _ => "Str_SearchScope_Window"
        };

        private void SearchScopeMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu) return;
            menu.Items.Clear();
            string[] glyphs = { "\uE737", "\uE8F1", "\uE7F4" };
            for (int scope = 0; scope < 3; scope++)
            {
                int selectedScope = scope;
                var icon = new TextBlock { Text = glyphs[scope], FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 13 };
                icon.SetBinding(TextBlock.ForegroundProperty, InheritedForeground());
                var item = new MenuItem
                {
                    Header = Loc(SearchScopeKey(scope)), Icon = icon,
                    IsCheckable = true, IsChecked = _titleSearchScope == scope
                };
                item.Click += (_, _) =>
                {
                    _titleSearchScope = selectedScope;
                    if (Pane.Active != null) Pane.Active.PipeFiles = null;
                    if (!_searchOpen) ToggleSearchPanel();
                    else ApplySearchPanel(false);
                    FocusFirstTerm();
                };
                menu.Items.Add(item);
            }
        }

        private void SearchScopeButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = SearchScopeButton };
            menu.Opened += SearchScopeMenu_Opened;
            menu.IsOpen = true;
        }

        private void TitleSearchPopup_Closed(object sender, EventArgs e)
        {
            _searchOpen = false;
            SearchPanelBtn.Tag = null;
        }

        private void TitleSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                TitleSearchPopup.IsOpen = false;
                SearchPanelBtn.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.OriginalSource is ComboBoxItem
                    || e.OriginalSource is ComboBox { IsDropDownOpen: true }
                    || e.OriginalSource is System.Windows.Controls.Primitives.DatePickerTextBox) return;
                Search_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private List<string> ResolveTitleSearchRoots()
        {
            IEnumerable<string> roots;
            if (_titleSearchScope == 2)
                roots = DemoMode ? new[] { @"C:\", @"D:\" } : DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);
            else if (_titleSearchScope == 1)
                roots = new[] { LeftPane, RightPane }.SelectMany(p => p.Tabs)
                    .Where(t => t.Procs == null && t.Events == null && t.Registry == null && t.Perf == null && t.Storage == null)
                    .Select(SearchFolderFor);
            else
            {
                var active = Pane.Active;
                string folder = active?.RootPath ?? string.Empty;
                if (active != null && (active.Term != null || active.Editor != null))
                    folder = SearchFolderFor(active);
                roots = new[] { folder };
            }
            return Services.SearchRoots.Normalize(roots.Where(path => DemoMode || Directory.Exists(path)));
        }

        private static string SearchFolderFor(Models.SearchTab tab)
        {
            string folder = string.IsNullOrEmpty(tab.CurrentFolder) ? tab.RootPath : tab.CurrentFolder;
            return tab.LaunchProfile?.BrowsePath(folder) ?? folder;
        }
    }
}
