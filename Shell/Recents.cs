using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

// Recently visited folders. Partial of MainWindow.
//
// A window-wide list, not a per-tab one. Per tab you already have Back and Forward, and they
// answer a different question - "where was I a moment ago" rather than "where do I keep going".
// The whole point of a recents list is that it survives closing the tab you were in.
//
// Stored the same way bookmarks are: one setting, paths joined by a character that cannot appear
// in one. Bookmarks are places you CHOSE to keep; this is places you happened to be, which is
// why they are separate lists and why this one silently forgets its tail.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private const string SetRecents = "RecentFolders";
        private const int RecentsMax = 15;

        private readonly List<string> _recents = [];

        private void InitRecents()
        {
            string saved = Services.ThemeManager.GetSetting(SetRecents) ?? string.Empty;
            foreach (string p in saved.Split([BookmarkSep], StringSplitOptions.RemoveEmptyEntries))
                if (_recents.Count < RecentsMax) _recents.Add(p);
        }

        /// <summary>
        /// Note that <paramref name="folder"/> was visited. Called from every navigation.
        /// </summary>
        /// <remarks>
        /// Most-recent-first, and re-visiting a folder MOVES it up rather than adding a second
        /// entry - a list where the place you use constantly appears nine times is not a list.
        /// This PC is skipped: it is a sentinel rather than a path, it is already pinned in the
        /// bookmarks by default, and it is one click from anywhere in the tree.
        /// </remarks>
        internal void RecordRecent(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || IsThisPc(folder)) return;   // Browse.cs

            _recents.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
            _recents.Insert(0, folder!);
            while (_recents.Count > RecentsMax) _recents.RemoveAt(_recents.Count - 1);

            Services.ThemeManager.SetSetting(SetRecents,
                string.Join(BookmarkSep.ToString(), _recents));
        }

        /// <summary>
        /// Fill and drop the recents menu. Rebuilt on every open rather than kept in step,
        /// because it is fifteen rows and it changes on every navigation.
        /// </summary>
        internal void Recents_Click(FilePane pane)
        {
            var menu = pane.RecentsMenu;
            menu.Items.Clear();

            // Folders that have since been deleted or unplugged are dropped rather than shown
            // greyed: a recents list is a shortcut, and a shortcut that cannot be taken is
            // just a row you have to read past every time.
            var live = _recents.Where(Directory.Exists).ToList();

            if (live.Count == 0)
            {
                var empty = new MenuItem { IsEnabled = false };
                empty.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Recents_Empty");
                menu.Items.Add(empty);
            }
            else
            {
                int rows = live.Count;   // live folder rows still in the menu, for the last-X close

                foreach (string p in live)
                {
                    string path = p;   // captured per row, not per loop

                    // Name on the row, full path underneath as the tooltip. Fifteen full paths
                    // makes a menu as wide as the window and all of them start "C:\Users\...".
                    // The header is a Grid rather than a string so a remove-X can right-align
                    // on the row - name in the star column, X in the Auto.
                    var name = new TextBlock
                    {
                        Text = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n
                             ? n : path,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var head = new Grid();
                    head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(name, 0);
                    head.Children.Add(name);

                    var item = new MenuItem { Header = head, ToolTip = path };

                    // The term/filter chips' exact X language: DangerButton, lowercase x, the
                    // GLYPH reddens on hover - never a filled block. Drops just this entry.
                    // Its Click never reaches the MenuItem (the Button captures the mouse), so
                    // the menu stays open for removing several in a row.
                    var remove = new Button
                    {
                        Content = "x", Width = 18, Height = 18,
                        Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0),
                        Style = (Style)FindResource("DangerButton"),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    remove.Click += (_, re) =>
                    {
                        re.Handled = true;
                        _recents.RemoveAll(q => string.Equals(q, path, StringComparison.OrdinalIgnoreCase));
                        Services.ThemeManager.SetSetting(SetRecents,
                            string.Join(BookmarkSep.ToString(), _recents));
                        menu.Items.Remove(item);
                        // Removing the last folder row leaves only the separator and Clear -
                        // close instead; the next open shows the empty row.
                        if (--rows == 0) menu.IsOpen = false;
                    };
                    Grid.SetColumn(remove, 1);
                    head.Children.Add(remove);

                    // 20, matching the tab strip. The brand icons carry a drop shadow inside their
                    // own box, so a 16px slot left the visible folder noticeably smaller than the
                    // menu text beside it (2026-08-08). The -2 side margins are what make
                    // 20 survive the shared MenuItem template's FIXED 16px icon gutter: they take
                    // the Image's DESIRED width down to 16 so it fits the column, while the
                    // arrange still draws the full 20 centered on the slot - without them the
                    // layout clip sheared 4px off the icon's edge (2026-08-09). The spill
                    // lands in the row's own 8px left padding and the header's 6px gap, so
                    // nothing else moves.
                    var icon = new Image
                    {
                        Width = 20, Height = 20,
                        Margin = new Thickness(-2, 0, -2, 0),
                        Source = Services.IconCache.For(path, 20, isDirectory: true),
                    };
                    item.Icon = icon;
                    item.Click += (_, _) => GoRecent(path);
                    menu.Items.Add(item);
                }

                menu.Items.Add(new Separator());
                var clear = new MenuItem();
                clear.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Recents_Clear");
                clear.Click += (_, _) =>
                {
                    _recents.Clear();
                    Services.ThemeManager.SetSetting(SetRecents, string.Empty);
                };
                menu.Items.Add(clear);
            }

            menu.PlacementTarget = pane.RecentsBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Navigates the pane the chevron belongs to, which is not necessarily the focused one -
        // clicking a control inside a pane focuses it first (FilePane's ctor), so by the time
        // this runs `Pane` is already the right one.
        private void GoRecent(string path)
        {
            if (!Directory.Exists(path)) return;
            _ = NavigateTo(path);   // Browse.cs
        }

        /// <summary>Hide the chevron on a search tab, where there is no folder history to show.</summary>
        internal void UpdateRecentsButton()
        {
            foreach (var p in new[] { LeftPane, RightPane })
                p.RecentsBtn.Visibility = p.Active?.IsBrowsing == true
                                        ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
