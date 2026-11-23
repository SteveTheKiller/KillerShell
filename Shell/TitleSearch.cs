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

        private static readonly string[] SearchScopeIds = { "window", "tabs", "computer" };

        /// <summary>
        /// The scope card: one tile per scope with the current one filled, and while the active tab holds results a
        /// fourth tile that searches within them (filled while it does).
        /// </summary>
        internal ContextMenu SearchScopeCard(ContextMenu? into = null)
        {
            var card = into != null ? Controls.CardMenus.Prepare(into) : Controls.CardMenus.TileCard();
            card.Tag = "searchscope";
            Controls.CardMenus.WatchKeyboardStart(card);
            string[] glyphs = { "\uE737", "\uE8F1", "\uE7F4" };
            var active = Pane.Active;
            var tiles = new List<KillerFamily.Controls.CardTile>();
            for (int scope = 0; scope < 3; scope++)
            {
                int selectedScope = scope;
                tiles.Add(new KillerFamily.Controls.CardTile(glyphs[scope], Loc(SearchScopeKey(scope)), null, () =>
                {
                    _titleSearchScope = selectedScope;
                    if (Pane.Active != null)
                    {
                        Pane.Active.PipeFiles = null;
                        Pane.ScopePathLabel.Text = string.IsNullOrEmpty(Pane.Active.RootPath)
                            ? Loc("Str_Scope_Empty") : Pane.Active.RootPath;
                    }
                    if (!_searchOpen) ToggleSearchPanel();
                    else ApplySearchPanel(false);
                    FocusFirstTerm();
                }) { Id = SearchScopeIds[scope], Checked = active?.PipeFiles == null && _titleSearchScope == scope });
            }
            if (active?.PipeFiles != null || (active != null && !active.IsBrowsing && active.Results.Count > 0))
            {
                tiles.Add(new KillerFamily.Controls.CardTile("\uE71C", Loc("Str_Menu_PipeTab"), "Ctrl+Shift+F", () =>
                {
                    if (Pane.Active != null && !Pane.Active.IsBrowsing && Pane.Active.Results.Count > 0)
                        PipeButton_Click(this, new RoutedEventArgs());
                    else FocusSearchTerms();
                }) { Id = "within", Checked = active.PipeFiles != null });
            }
            card.Items.Add(Controls.CardMenus.Tiles(card, tiles));
            return card;
        }

        private void SearchScopeButton_Click(object sender, RoutedEventArgs e)
        {
            var card = SearchScopeCard();
            SearchScopeButton.ContextMenu = card;
            OpenUnder(card, SearchScopeButton);
        }

        // A right-click on the title bar's search button offers the same card, refilled each time it opens.
        private void SearchPanelBtn_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (SearchPanelBtn.ContextMenu is { } menu) SearchScopeCard(menu);
        }

        private void TitleSearchPopup_Closed(object sender, EventArgs e)
        {
            _searchOpen = false;
            SearchPanelBtn.Tag = null;
            SearchPanelBtn.Content = "\uE721";
            SearchPanelBtn.SetResourceReference(ToolTipProperty, "Str_TT_SearchPanel");
            TitleSearchSurface.BeginAnimation(OpacityProperty, null);
            TitleSearchSurface.Opacity = 0;
        }

        private void TitleSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                // An open dropdown closes itself first.
                if (e.OriginalSource is ComboBoxItem || e.OriginalSource is ComboBox { IsDropDownOpen: true }) return;
                if (PatternHelpOverlay.Visibility == Visibility.Visible)
                {
                    PatternHelpClose_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                TitleSearchPopup.IsOpen = false;
                SearchPanelBtn.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.OriginalSource is ComboBoxItem
                    || e.OriginalSource is ComboBox { IsDropDownOpen: true }
                    || e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase
                    || e.OriginalSource is System.Windows.Controls.Primitives.DatePickerTextBox) return;
                Search_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        /// <summary>
        /// The folders the chosen scope names, before anything checks they exist. Cheap and safe
        /// on the UI thread: listing drives and reading tab folders does not touch the disk.
        /// The scope tooltip shows these as they are.
        /// </summary>
        private List<string> TitleSearchRootCandidates()
        {
            IEnumerable<string> roots;
            if (_titleSearchScope == 2)
                roots = DemoMode ? new[] { @"C:\", @"D:\" } : DriveInfo.GetDrives().Select(d => d.Name);
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
            return Services.SearchRoots.Normalize(roots);
        }

        /// <summary>
        /// The candidates that exist, checked off the UI thread with a time limit per folder
        /// (SearchRoots.ProbeAsync). Directory.Exists on a drive root is the same readiness test
        /// DriveInfo.IsReady made, and both could stall for seconds on a dead network drive.
        /// </summary>
        private async System.Threading.Tasks.Task<List<string>> ResolveTitleSearchRootsAsync(List<string> candidates)
        {
            if (DemoMode) return candidates;
            return await Services.SearchRoots.ProbeAsync(candidates, Directory.Exists, Services.SearchRoots.ProbeTimeout);
        }

        private static string SearchFolderFor(Models.SearchTab tab)
        {
            string folder = string.IsNullOrEmpty(tab.CurrentFolder) ? tab.RootPath : tab.CurrentFolder;
            return tab.LaunchProfile?.BrowsePath(folder) ?? folder;
        }
    }
}
