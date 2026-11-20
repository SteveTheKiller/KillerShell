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

        // The saved-place menu lives in the row template, so it is set up per row as it opens.
        // Keyboard opening on a focused place: WPF raises ContextMenuOpening on the list item, which
        // has no menu of its own, so hand it to the row's card and anchor that card to the row.
        private void BookmarkItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (e.Handled || !ReferenceEquals(e.OriginalSource, sender) || sender is not System.Windows.Controls.ListBoxItem item) return;
            if (FindDescendant<System.Windows.Controls.Border>(item) is not { ContextMenu: { } card } row) return;
            e.Handled = true;
            BookmarkRow_ContextMenuOpening(row, e);
            card.PlacementTarget = row;
            card.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            card.IsOpen = true;
        }

        private void BookmarkRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: Bookmark b } row) return;

            // This PC and the Recycle Bin are places, not folders: a terminal, a scan or a
            // Properties sheet has nowhere to point, so on those only Remove is lit.
            bool sentinel = IsPlaceSentinel(b.Path);   // Browse.cs
            var card = row.ContextMenu;
            if (card == null) return;
            Controls.CardMenus.Prepare(card);

            // The card names the place it acts on, the same header the tree card draws.
            var source = sentinel ? null : Services.IconCache.For(b.Path, 32, isDirectory: true);
            var icon = source == null ? null : new Image { Source = source, Width = 32, Height = 32 };
            card.Items.Add(Controls.CardMenus.HeaderSlot(card, Controls.CardMenus.Header(icon, b.NameLabel, sentinel ? "" : b.Path)));

            // The tree card's verbs on the place's path. Each runs once the card has closed, for the
            // same reason as the tree: Search here moves focus into the search box.
            string path = b.Path;
            bool folder = !sentinel;
            bool nicknamed = folder && Services.Nicknames.Get(path).Length > 0;
            KillerFamily.Controls.CardTile Tile(string id, int glyph, string captionKey, string? shownKey, Action run, bool enabled)
                => Controls.CardMenus.Tile(id, glyph, captionKey, shownKey, null, () => AfterMenuCloses(run), enabled);   // ResultsMenu.cs
            card.Items.Add(Controls.CardMenus.Tiles(card,
            [
                Tile("newtab", 0xE710, "Str_Menu_OpenInNewTab", "Str_Card_NewTab", () => OpenFolderInNewTab(path), folder),
                Tile("explorer", 0xE838, "Str_Menu_ShowExplorer", "Str_Card_Explorer", () => ShowFolderInExplorer(path), folder),
                Tile("search", 0xE721, "Str_Menu_SearchHere", "Str_Card_Search", () => SearchInFolder(path), folder),
                Tile("terminal", 0xE756, "Str_Menu_OpenTerminal", "Str_Card_Terminal",
                    () => OpenShell(Terminal.TerminalProfileStore.ForFolder(path), path), folder),
                // E7EF (admin shield), not the plain E756: two identical icons on two actions read as one.
                Tile("terminaladmin", 0xE7EF, "Str_Menu_OpenTerminalAdmin", null, () => BookmarkTerminalAdmin_Click(row, e), folder),
                Tile("analyze", 0xEDA2, "Str_Menu_AnalyzeStorage", null, () => BookmarkAnalyze_Click(row, e), folder),
                Tile("copypath", 0xE8C8, "Str_Menu_CopyPath", "Str_Card_Path", () => PutOnClipboard(path, "Str_Status_Copied", "1"), folder),
                Tile("nick", 0xE8EC, nicknamed ? "Str_Menu_ChangeNickname" : "Str_Menu_AddNickname", null, () => EditNickname(path), folder),
                Tile("nickdel", 0xE738, "Str_Menu_RemoveNickname", null, () => RemoveNickname(path), nicknamed),
                Tile("properties", 0xE946, "Str_Menu_Properties", null, () => BookmarkProperties_Click(row, e), folder),
                Tile("remove", 0xE74D, "Str_Menu_RemoveFavorite", null, () => RemoveBookmark(path), true),
            ]));
        }

        // The listing acts on the item the menu opened on (the seed), not the whole selection:
        // a nickname belongs to one item.
        internal void MenuNickname_Click(object sender, RoutedEventArgs e)       => EditNickname(_menuSeed?.FilePath);
        internal void MenuNicknameRemove_Click(object sender, RoutedEventArgs e) => RemoveNickname(_menuSeed?.FilePath);
    }
}
