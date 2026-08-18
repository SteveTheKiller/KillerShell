using System;
using System.IO;
using System.Windows;

namespace KillerShell.Shell
{
    // Two listing preferences that belong to the app rather than to a tab: whether hidden and
    // system entries are shown, and whether folders are pinned above files. Partial of MainWindow.
    //
    // Window-wide on purpose. Explorer and Total Commander both treat these as view settings
    // rather than per-location state. Both live panes are refreshed from the same setting, so
    // the button cannot claim one state while a background pane still shows the other.
    public partial class MainWindow
    {
        // Read by ListFolder (Browse.cs), which runs off the UI thread, so these stay plain
        // fields with no UI coupling.
        // NOT read by the tree. The tree has its own switch, MainWindow.TreeShowHidden
        // (FolderTree.cs), which also covers System and dot-prefixed folders. The two are
        // deliberately independent: a clean tree over a listing that shows everything is the
        // combination that is actually wanted.
        internal static bool ShowHidden   { get; private set; }
        internal static bool FoldersOnTop { get; private set; } = true;

        /// <summary>
        /// The listing's one definition of a hidden item. Windows has two attributes that mean
        /// "keep this out of an ordinary folder view", while portable tools and developer
        /// projects conventionally use a leading dot instead. The toolbar switch controls all
        /// three together, for files and folders alike.
        /// </summary>
        internal static bool ShouldHideListingEntry(string name, FileAttributes attributes) =>
            !ShowHidden &&
            (((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) ||
             name.StartsWith(".", StringComparison.Ordinal));

        private void InitViewOptions()
        {
            ShowHidden   = Services.ThemeManager.GetSetting("ShowHidden")   == "1";
            FoldersOnTop = Services.ThemeManager.GetSetting("FoldersOnTop") != "0";   // default on
            UpdateViewOptionButtons();
        }

        // Both toggles are WINDOW-wide settings, so both panes have to show the same state -
        // hence ForEachPane rather than writing through `Pane` once (Panes.cs).
        private void UpdateViewOptionButtons() => ForEachPane(UpdateViewOptionButtonsInPane);

        private void UpdateViewOptionButtonsInPane()
        {
            // E7B3 is the "hidden" eye, E890 the open one, so the glyph says what you are
            // currently looking at rather than what the click would do.
            Pane.ShowHiddenBtn.Content = ((char)(ShowHidden ? 0xE890 : 0xE7B3)).ToString();
            Pane.ShowHiddenBtn.Tag     = ShowHidden ? "on" : null;
            Pane.FoldersTopBtn.Tag     = FoldersOnTop ? "on" : null;
        }

        /// <summary>
        /// Shows the details-view location column only when rows can come from different places.
        /// Called wherever a tab's browsing state can change (Browse.cs, Tabs.cs).
        /// </summary>
        internal void UpdateLocationColumn()
        {
            bool browsing = _active != null && _active.IsBrowsing;

            // A flag rather than writing a width: the column is draggable now, and assigning 0
            // over the top would throw away whatever the user had sized it to the moment they
            // opened a folder. Hidden and zero-wide look identical; only one of them remembers.
            Pane.ViewState.LocationHidden = browsing;

            UpdateBrowseChrome(browsing);
        }

        /// <summary>
        /// The toolbar bits that only mean something over search results. Browsing hides them
        /// rather than leaving controls on screen that do nothing useful where you are standing.
        /// </summary>
        private void UpdateBrowseChrome(bool browsing)
        {
            // Pipe opens a NEW TAB scoped to the listed files. Over a folder listing the funnel
            // reads as "filter these rows", so the tab it opens comes as a surprise; over search
            // results, which is what it was built for, it reads correctly.
            Pane.PipeBtn.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;

            // "as found" is the ENGINE'S discovery order, which only exists because a search
            // streams hits in as it walks. A folder is enumerated in one pass, so there is no
            // discovery order to show - the entry goes away and name takes over.
            Pane.SortFoundItem.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;

            if (browsing && _active != null && _active.SortIndex == 0)
            {
                _active.SortIndex = 1;               // name
                ApplySort(_active);                  // Results.cs
            }
        }

        internal async void ShowHidden_Click(object sender, RoutedEventArgs e)
        {
            ShowHidden = !ShowHidden;
            Services.ThemeManager.SetSetting("ShowHidden", ShowHidden ? "1" : "0");
            UpdateViewOptionButtons();

            // ONLY the listing is re-read. This used to refresh the tree as well, on the
            // reasoning that both were built with the old filter - true when they shared one
            // switch, and false since the tree got its own (MainWindow.TreeShowHidden,
            // FolderTree.cs). The tree does not read ShowHidden at all now, so refreshing it
            // here changed nothing about what it contained and only made the sidebar visibly
            // reflow on a toggle that is none of its business.
            // The setting and both toolbar buttons are window-wide, so both visible listings
            // must change with them. Refresh the other pane first and the focused pane last: a
            // navigation writes through the Pane indirection, and finishing on the original pane
            // leaves the shared window chrome describing the place the user is actually in.
            var keep = Pane;
            try
            {
                foreach (var pane in LivePanes())
                {
                    if (ReferenceEquals(pane, keep)) continue;
                    FocusPaneQuiet(pane);
                    var tab = pane.Active;
                    if (tab?.IsBrowsing == true && !string.IsNullOrEmpty(tab.CurrentFolder))
                        await NavigateTo(tab.CurrentFolder, record: false, keepSelection: true); // Browse.cs
                }

                FocusPaneQuiet(keep);
                var active = keep.Active;
                if (active?.IsBrowsing == true && !string.IsNullOrEmpty(active.CurrentFolder))
                    await NavigateTo(active.CurrentFolder, record: false, keepSelection: true); // Browse.cs
                else if (active != null)
                    ActivateTab(active);   // restore shared chrome after refreshing the other pane
            }
            finally
            {
                FocusPaneQuiet(keep);
                UpdatePaneFocusRing();
            }
        }

        internal void FoldersTop_Click(object sender, RoutedEventArgs e)
        {
            FoldersOnTop = !FoldersOnTop;
            Services.ThemeManager.SetSetting("FoldersOnTop", FoldersOnTop ? "1" : "0");
            UpdateViewOptionButtons();

            // Sort only - no re-listing needed, and every tab's view has to be reprogrammed or
            // the background ones would keep the old grouping until they were next touched.
            foreach (var t in _tabs) ApplySort(t);   // Results.cs
        }
    }
}
