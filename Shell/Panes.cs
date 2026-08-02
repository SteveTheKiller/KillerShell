using System.Collections.ObjectModel;
using KillerShell.Models;

namespace KillerShell.Shell
{
    // Which pane the window's commands act on. Partial of MainWindow.
    //
    // The focused pane moves with the click and every command keeps working untouched, which is
    // why the named controls are reached through this one property rather than being referenced
    // directly at 133 call sites. Landing that indirection BEFORE the split is what made dual
    // pane a layout change instead of another sweep through every file - Tabs.cs, Results.cs and
    // FileCommands.cs needed nothing, because they were already asking for "the focused pane".
    public partial class MainWindow
    {
        private FilePane? _focus;

        /// <summary>
        /// The pane every command acts on. Resolved on first use rather than in the ctor: the
        /// panes are built by InitializeComponent, and some initialization runs before that
        /// finishes.
        /// </summary>
        internal FilePane Pane => _focus ??= LeftPane;

        /// <summary>
        /// Point every command at <paramref name="pane"/>. Raised by a click anywhere inside a
        /// pane (FilePane's ctor).
        /// </summary>
        internal void FocusPane(FilePane pane)
        {
            if (_focus == pane) return;
            _focus = pane;

            // The window chrome - search panel, footer line, nav buttons - shows the focused
            // pane's tab, so moving focus has to re-point it at that pane's active tab.
            if (pane.Active != null) ActivateTab(pane.Active);

            UpdatePaneFocusRing();   // DualPane.cs - the accent ring follows focus
        }

        /// <summary>
        /// Move focus WITHOUT re-pointing the window chrome or the focus ring. Used only while
        /// seeding a pane that is not the one the user is looking at (DualPane.cs): the point is
        /// to run Tabs.cs's own CreateTab/ActivateTab against a chosen pane and then put focus
        /// straight back, so the visible chrome must not follow the detour.
        /// </summary>
        private void FocusPaneQuiet(FilePane pane) => _focus = pane;

        /// <summary>Panes currently on screen. Just the left one until the second pane opens.</summary>
        private System.Collections.Generic.IEnumerable<FilePane> LivePanes()
        {
            yield return LeftPane;
            if (DualPane) yield return RightPane;   // DualPane.cs
        }

        /// <summary>
        /// Run <paramref name="apply"/> once per live pane, with focus pointed at each in turn,
        /// then hand focus back.
        ///
        /// This exists for WINDOW-WIDE settings that are mirrored into per-pane controls - the
        /// results view mode, the show-hidden and folders-on-top toggles. Those write through
        /// `Pane`, so on their own they only ever reached the focused pane: the second pane came
        /// up in whatever view its XAML defaulted to, with its view buttons unlit, and changing
        /// the view while focused on one pane left the other stale.
        ///
        /// Per-TAB state is not this. That legitimately belongs to one pane and is applied by
        /// ActivateTab.
        /// </summary>
        private void ForEachPane(System.Action apply)
        {
            var keep = Pane;
            foreach (var p in LivePanes()) { FocusPaneQuiet(p); apply(); }
            FocusPaneQuiet(keep);
        }

        // Tabs belong to a PANE, not to the window (FilePane.Tabs / FilePane.Active). These two
        // keep the old field names so the ~100 call sites across Tabs.cs, Session.cs, Results.cs
        // and the rest read exactly as they did - they now just resolve against whichever pane
        // has focus instead of against one window-wide collection.
        private ObservableCollection<SearchTab> _tabs => Pane.Tabs;

        private SearchTab _active
        {
            get => Pane.Active;
            set => Pane.Active = value;
        }
    }
}
