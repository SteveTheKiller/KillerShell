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
                foreach (string p in live)
                {
                    string path = p;   // captured per row, not per loop

                    // Name on the row, full path underneath as the tooltip. Fifteen full paths
                    // makes a menu as wide as the window and all of them start "C:\Users\...".
                    var item = new MenuItem
                    {
                        Header  = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n
                                ? n : path,
                        ToolTip = path,
                    };
                    var icon = new Image
                    {
                        Width = 16, Height = 16,
                        Source = Services.IconCache.For(path, 16, isDirectory: true),
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
