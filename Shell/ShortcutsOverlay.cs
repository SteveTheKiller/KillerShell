using System.Windows;
using System.Windows.Controls;

namespace KillerShell.Shell
{
    // ═══════════════════════════════════════════════════════════
    //  KEYBOARD SHORTCUTS  -  single source of truth
    // ═══════════════════════════════════════════════════════════
    // One table feeds BOTH views on the shortcuts card: the grouped list here, and the visual
    // keyboard in KeyboardMapOverlay.cs. The card used to be 22 hand-written XAML rows, which
    // meant every new binding had to be added in three places and the list drifted the moment
    // one was missed - F12 was in the code and nowhere on the card.
    //
    // Rows are grouped by category, and a category's color is a theme brush (KsCat* in
    // Themes/*.xaml) shared with the keyboard's keycap borders, so a group reads the same in
    // both views.
    public partial class MainWindow
    {
        // (category, the gesture as it should read on the card, label resource key)
        private static readonly (string Cat, string Keys, string Label)[] KsRows =
        [
            ("Search", "Enter",          "Str_Ks_Run"),
            ("Search", "Ctrl+E",         "Str_Ks_FocusSearch"),
            ("Search", "Ctrl+Shift+A",   "Str_Ks_AddTerm"),
            ("Search", "Shift+F7",       "Str_Ks_AddFilter"),
            ("Search", "Alt+C",          "Str_Ks_CaseSensitive"),
            ("Search", "Ctrl+F",         "Str_Ks_FilterResults"),
            ("Search", "Ctrl+Shift+F",   "Str_Ks_Pipe"),

            ("Nav",    "Alt+Left / Right", "Str_Ks_BackForward"),
            ("Nav",    "Backspace",      "Str_Ks_Back"),
            ("Nav",    "Alt+Up",         "Str_Ks_Up"),
            ("Nav",    "Ctrl+L / F4",    "Str_Ks_Address"),
            ("Nav",    "Ctrl+O",         "Str_Ks_Folder"),
            ("Nav",    "Ctrl+B",         "Str_Ks_Bookmarks"),
            ("Nav",    "Alt+1-0",        "Str_Ks_JumpBookmark"),

            ("Tabs",   "Ctrl+N",         "Str_Ks_NewWindow"),
            ("Tabs",   "Ctrl+T",         "Str_Ks_NewTab"),
            ("Tabs",   "Ctrl+W",         "Str_Ks_CloseTab"),
            ("Tabs",   "Ctrl+Tab",       "Str_Ks_NextTab"),
            ("Tabs",   "Ctrl+1-9",       "Str_Ks_JumpTab"),

            ("View",   "F5",             "Str_Ks_Refresh"),
            ("View",   "F10",            "Str_Ks_MenuBar"),
            ("View",   "F11",            "Str_TT_DualPane"),
            ("View",   "Ctrl+H",         "Str_TT_ShowHidden"),
            ("View",   "Ctrl+Shift+S",   "Str_Ks_SearchPanel"),
            ("View",   "Ctrl+Right",     "Str_Ks_ExpandAll"),
            ("View",   "Ctrl+Left",      "Str_Ks_CollapseAll"),

            ("File",   "F9",             "Str_Ks_ExportHtml"),
            ("File",   "Shift+F9",       "Str_Ks_ExportCsv"),

            // Shells open as tabs in the focused pane, so they are grouped with tabs rather
            // than given a category of their own - one more heading for three rows would cost
            // a Str_Ks_Cat key in ten locale files to say the same thing.
            // Ctrl+` and Ctrl+Alt+` are real aliases in the handler (MainWindow.xaml.cs), kept
            // because VS Code and Windows Terminal both train that chord. Listed on the same row
            // rather than their own, the way Ctrl+L / F4 and Alt+Left / Right already are - a
            // second row would need a second locale key to say the same sentence twice.
            ("Tabs",   "F8 / Ctrl+`",    "Str_Ks_Shell"),
            ("Tabs",   "Shift+F8",       "Str_Ks_ShellCmd"),
            ("Tabs",   "Ctrl+F8 / Ctrl+Alt+`", "Str_Ks_ShellAdmin"),

            ("Edit",   "Ctrl+A",         "Str_Ks_SelectAll"),
            ("Edit",   "Ctrl+X / C / V", "Str_Ks_CutCopyPaste"),
            ("Edit",   "F2",             "Str_Ks_Rename"),
            ("Edit",   "Delete",         "Str_Ks_Recycle"),
            ("Edit",   "Shift+Delete",   "Str_Ks_DeleteForever"),
            ("Edit",   "Ctrl+Shift+N",   "Str_Ks_NewFolder"),
            ("Edit",   "Ctrl+Shift+L",   "Str_Ks_Clear"),
            ("Edit",   "Esc",            "Str_Ks_Esc"),

            // Results context-menu commands. These reuse the MENU's own Str_Menu_* strings as
            // their labels rather than adding a parallel set of Str_Ks_* keys: the card would
            // only be restating the menu row word for word, and duplicating the wording across
            // ten locale files is how the two drift apart. Locale key count is unchanged.
            ("File",   "Enter",           "Str_Menu_OpenFile"),
            ("File",   "F7",              "Str_Menu_Edit"),
            ("File",   "Ctrl+,",          "Str_Prof_Edit"),
            ("File",   "Ctrl+Shift+O",    "Str_Menu_OpenWith"),
            ("File",   "Ctrl+Shift+Enter","Str_Menu_OpenAdmin"),
            ("File",   "F6",              "Str_Menu_ShowExplorer"),
            ("File",   "F3",              "Str_Menu_SearchHere"),
            ("File",   "Ctrl+Shift+E",    "Str_Menu_ExcludeFolder"),
            ("File",   "Ctrl+D",          "Str_Menu_AddFavorite"),
            ("File",   "Alt+Enter",       "Str_Menu_Properties"),
            ("File",   "Shift+F10",       "Str_Menu_ShellMenu"),

            ("Edit",   "Ctrl+Shift+C",   "Str_Menu_CopyPath"),
            ("Edit",   "Ctrl+Shift+M",   "Str_Menu_CopyName"),
            ("Edit",   "Ctrl+Shift+D",   "Str_Menu_CopyFolder"),
            ("Edit",   "Ctrl+Shift+Y",   "Str_Menu_CopyLines"),
            ("Edit",   "Ctrl+Shift+H",   "Str_Menu_CopyHash"),

            ("Help",   "F1",             "Str_Ks_Help"),
            ("Help",   "F12",            "Str_Ks_About"),
        ];

        // Display order of the groups. Search first because that is what the app is for; Help
        // last because you already found it if you are reading this card.
        private static readonly string[] KsCatOrder =
            ["Search", "Nav", "Tabs", "View", "File", "Edit", "Help"];

        /// <summary>Resource key for a category's heading, e.g. "Nav" -> Str_Ks_CatNav.</summary>
        internal static string KsCatLabelKey(string cat) => "Str_Ks_Cat" + cat;

        private bool _ksListBuilt;

        /// <summary>
        /// Fills the card's list from <see cref="KsRows"/>, grouped under colored headings.
        /// Built once, lazily - the card is not open at startup and most sessions never open it.
        /// Every brush and label is wired with SetResourceReference so a theme or language
        /// switch repaints it live rather than needing a rebuild.
        /// </summary>
        private void BuildShortcutsList()
        {
            if (_ksListBuilt) return;
            _ksListBuilt = true;

            ShortcutListHost.Children.Clear();

            // Two columns side by side rather than one long scroll. Categories are dealt into the
            // left column until it holds about half the rows, then the rest go right - so reading
            // order is still top-left down, then top-right down, and a group is never split
            // across the fold.
            int total = KsRows.Length, running = 0;

            var left  = new StackPanel();
            var right = new StackPanel();

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            columns.Children.Add(left);
            columns.Children.Add(right);
            ShortcutListHost.Children.Add(columns);

            foreach (string cat in KsCatOrder)
            {
                var column = running * 2 < total ? left : right;
                foreach (var c in KsRows) if (c.Cat == cat) running++;

                var heading = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 10,
                    FontWeight = FontWeights.Bold,
                    Margin     = new Thickness(0, 10, 0, 6),
                };
                heading.SetResourceReference(TextBlock.TextProperty, KsCatLabelKey(cat));
                heading.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + cat);
                column.Children.Add(heading);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int row = 0;
                foreach (var r in KsRows)
                {
                    if (r.Cat != cat) continue;
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var keys = new TextBlock
                    {
                        Text       = r.Keys,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize   = 12,
                        Margin     = new Thickness(0, 0, 10, 7),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    keys.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                    Grid.SetRow(keys, row);
                    Grid.SetColumn(keys, 0);
                    grid.Children.Add(keys);

                    var desc = new TextBlock
                    {
                        FontSize     = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Margin       = new Thickness(0, 1, 0, 7),
                    };
                    desc.SetResourceReference(TextBlock.TextProperty, r.Label);
                    desc.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    Grid.SetRow(desc, row);
                    Grid.SetColumn(desc, 1);
                    grid.Children.Add(desc);

                    row++;
                }

                column.Children.Add(grid);
            }
        }
    }
}
