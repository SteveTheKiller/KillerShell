using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;   // SearchResult - the listing rows redrawn after a change

namespace KillerShell.Shell
{
    // ═══════════════════════════════════════════════════════════
    //  NICKNAMES  -  a label for a file or folder, nothing renamed on disk
    // ═══════════════════════════════════════════════════════════
    // Offered on every surface that draws an item: the folder tree, the saved places and the
    // listing. Each menu carries two rows found by Tag: "nick" (Add nickname, or Change
    // nickname once one is set) and "nickdel" (Remove nickname, only shown when there is one).
    // The store and the "Nickname (name)" label live in Services/Nicknames.cs.
    public partial class MainWindow
    {
        private bool _nicknamesHooked;

        /// <summary>Hooks this window to nickname changes made in any window. Called from
        /// InitBookmarks; unhooked again when the window closes.</summary>
        private void HookNicknames()
        {
            if (_nicknamesHooked) return;
            _nicknamesHooked = true;
            Services.Nicknames.Changed += OnNicknameChanged;
            Closed += (_, _) => Services.Nicknames.Changed -= OnNicknameChanged;
        }

        /// <summary>Sets the add/change/remove rows of an item menu for the item it opened on.
        /// A null path (empty pane space) hides both.</summary>
        private static void SyncNicknameMenu(ItemsControl? menu, string? path)
        {
            if (menu == null) return;
            bool has = !string.IsNullOrEmpty(path) && Services.Nicknames.Get(path).Length > 0;
            foreach (var mi in menu.Items.OfType<MenuItem>())
            {
                switch (mi.Tag as string)
                {
                    case "nick":
                        mi.Visibility = string.IsNullOrEmpty(path) ? Visibility.Collapsed : Visibility.Visible;
                        mi.Header = Loc(has ? "Str_Menu_ChangeNickname" : "Str_Menu_AddNickname");
                        break;
                    case "nickdel":
                        mi.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                        break;
                }
            }
        }

        /// <summary>The rename prompt, asking for a nickname. An empty answer removes it.</summary>
        private void EditNickname(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var dlg = RenameDialog.ForNickname(Services.Nicknames.Get(path)); dlg.Owner = this;
            dlg.ShowDialog();
            if (dlg.Confirmed) Services.Nicknames.Set(path!, dlg.NewName);
        }

        private static void RemoveNickname(string? path)
        {
            if (!string.IsNullOrEmpty(path)) Services.Nicknames.Set(path!, null);
        }

        // Every surface redraws its labels. Cheap: a label is one dictionary lookup per row, and
        // only rows that are realized repaint.
        private void OnNicknameChanged(string path)
        {
            foreach (var pane in LivePanes())
                foreach (var tab in pane.Tabs)
                    foreach (SearchResult r in tab.Results) r.NotifyNickname();

            foreach (var root in _treeRoots) NotifyTree(root);

            foreach (var b in _bookmarks) b.NotifyNickname();
        }

        private static void NotifyTree(FolderNode node)
        {
            node.NotifyNickname();
            foreach (var child in node.Children) NotifyTree(child);
        }

        // ── Menu rows ────────────────────────────────────────────
        private void TreeNickname_Click(object sender, RoutedEventArgs e)       => EditNickname(TreeMenuPath());
        private void TreeNicknameRemove_Click(object sender, RoutedEventArgs e) => RemoveNickname(TreeMenuPath());

        private void BookmarkNickname_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: Bookmark b }) EditNickname(b.Path);
        }

        private void BookmarkNicknameRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: Bookmark b }) RemoveNickname(b.Path);
        }

        // The saved-place menu lives in the row template, so it is set up per row as it opens.
        private void BookmarkRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: Bookmark b } row)
                SyncNicknameMenu(row.ContextMenu, IsThisPc(b.Path) ? null : b.Path);
        }

        // The listing acts on the item the menu opened on (the seed), not the whole selection:
        // a nickname belongs to one item.
        internal void MenuNickname_Click(object sender, RoutedEventArgs e)       => EditNickname(_menuSeed?.FilePath);
        internal void MenuNicknameRemove_Click(object sender, RoutedEventArgs e) => RemoveNickname(_menuSeed?.FilePath);
    }
}
