using System.Windows;
using System.Windows.Media;
using KillerShell.Models;

// Task Manager tabs, and where they land. Partial of MainWindow.
//
// Same placement rule the shell and the editor already follow (TerminalTabs.cs / EditorTabs.cs):
// a new tab in the FOCUSED pane, nothing moved, nothing split. A process list has no folder of
// its own to argue for a different rule.
//
// Unlike the shell (many, one per working directory you want) and the editor (many, one per open
// file), a Task Manager tab has no per-instance identity worth keeping separate - two of them
// would show the exact same machine-wide list a heartbeat apart. So opening it is a SINGLETON:
// the rail button focuses an existing Task Manager tab anywhere in the window before it creates
// a new one. This is the one guess in this file worth double-checking against how Steve actually
// wants it to behave; Terminal/Editor's "always make a new one" rule was deliberately not copied
// here because it did not seem to fit a live system view the way it fits a shell or a document.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // E8FD (ViewAll/list): the same glyph Windows 11's own Task Manager uses for its
        // Processes tab, so this reads as "a list of running things" rather than a live-updating
        // gauge. E9D9 (Processing - a spinner, which DOES read as "live") is reserved for the
        // Performance tab (issue #1) when that gets built, so the two stay visually distinct
        // once they sit side by side on the rail.
        private static readonly string ProcessesGlyph = ((char)0xE8FD).ToString();

        // ═══════════════════════════════════════════════════════════
        //  OPEN
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Open the Task Manager tab, or switch to it if one is already open somewhere in the
        /// window. The rail's left click.
        /// </summary>
        internal void OpenTaskManager()
        {
            foreach (var pane in LivePanes())                 // Panes.cs
                foreach (var open in pane.Tabs)
                    if (open.IsProcessList)
                    {
                        FocusPane(pane);                       // Panes.cs
                        SwitchToTab(open);                     // Tabs.cs
                        return;
                    }

            CaptureTab(_active);                               // Tabs.cs - the outgoing tab keeps its state
            var tab = CreateProcessListTab();
            ActivateTab(tab);
        }

        private void TaskManagerRail_Click(object sender, RoutedEventArgs e) => OpenTaskManager();

        // Rail context menu (MainWindow.xaml): the same F9 / Ctrl+F9 choice the keyboard already
        // has, surfaced as a right-click so the tooltip does not have to spell out the elevated
        // path in prose. RailProcessesAdmin_Click reuses Elevation.cs's RelaunchElevatedProcesses
        // directly - same as Ctrl+F9 in MainWindow.xaml.cs.
        private void RailProcessesOpen_Click(object sender, RoutedEventArgs e) => OpenTaskManager();
        private void RailProcessesAdmin_Click(object sender, RoutedEventArgs e) => RelaunchElevatedProcesses();   // Elevation.cs

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        private SearchTab CreateProcessListTab()
        {
            var tab = CreateTab();                             // Tabs.cs - registers it in this pane

            var procs = new ProcessListControl();
            tab.Procs      = procs;
            tab.TabGlyph   = ProcessesGlyph;
            tab.Title      = Loc("Str_TabTitle_TaskManager");
            tab.IsBrowsing = false;

            // "Open file location" has to become a browse tab, and only the window can create
            // and navigate one - the control has no idea which pane or window it lives in
            // (ProcessListControl.cs).
            procs.OpenFileLocationRequested += folder => ProcessOpenFileLocation(folder);

            return tab;
        }

        /// <summary>
        /// Where a right-click's "Open file location" actually goes. Mirrors
        /// Bookmarks.cs OpenBookmark exactly, and for the same reason: NavigateTo rewrites the
        /// FOCUSED tab's own CurrentFolder/IsBrowsing bookkeeping, which is fine on a listing tab
        /// and wrong on a shell, a document or another Task Manager - none of those have a
        /// folder for that bookkeeping to mean anything about.
        /// </summary>
        private void ProcessOpenFileLocation(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) return;

            if (_active != null && (_active.IsTerminal || _active.IsEditor || _active.IsProcessList))
                ActivateTab(CreateTab());                      // Tabs.cs

            _ = NavigateTo(folder);                             // Browse.cs
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIVATION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Swap the pane between its listing and the Task Manager. Called from ActivateTab, so
        /// it runs on every tab switch in either pane.
        /// </summary>
        /// <remarks>
        /// Runs after ApplyEditorView and quietly re-makes the same decision ApplyTerminalView
        /// and ApplyEditorView already make about ResultsList - see the remark on
        /// ApplyEditorView (EditorTabs.cs) for why that redundancy is deliberate rather than a
        /// leftover.
        /// </remarks>
        private void ApplyProcessListView(SearchTab t)
        {
            bool procs = t.IsProcessList;

            Pane.ProcessListHost.Visibility = procs ? Visibility.Visible : Visibility.Collapsed;

            // MOVED rather than rebuilt: the control owns a live refresh timer, the filter text
            // and the grid's own sort/scroll state, and a fresh one per activation would throw
            // all three away on every tab switch, same as it would for a shell or a document.
            Pane.ProcessListSlot.Content = procs ? t.Procs : null;
            if (!procs) return;

            Pane.ResultsList.Visibility = Visibility.Collapsed;

            // Sorting, view mode and the details header all mean nothing over a process list -
            // it is not a file listing, so it gets the same treatment TerminalTabs.cs gives a
            // shell (ListingOnlyTools hidden wholesale).
            ApplyPaneToolbarMode(true);       // TerminalTabs.cs

            var procList = t.Procs!;
            // Focus has to wait for the swap to lay out, or it lands on an element that is still
            // collapsed and silently does nothing - same reason the shell and the editor both
            // defer their own focus call (TerminalTabs.cs / EditorTabs.cs).
            Dispatcher.BeginInvoke(new System.Action(() => procList.FocusFilter()),
                                   System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>Tear a Task Manager tab down when its tab closes. Called from FinishCloseTab.</summary>
        private void CloseProcessList(SearchTab t)
        {
            if (t.Procs == null) return;
            if (ReferenceEquals(Pane.ProcessListSlot.Content, t.Procs)) Pane.ProcessListSlot.Content = null;
            t.Procs.Shutdown();
            t.Procs = null;
        }

        /// <summary>
        /// Shutdown() every open Processes tab's control without closing the tabs themselves -
        /// called once from Session.cs OnClosing, right as the window really is going away
        /// (there is no per-tab FinishCloseTab pass on a plain window close, only when a tab is
        /// closed individually). A Processes tab left open at quit used to crash the app with
        /// RaceOnRCWCleanup: its background owner-lookup thread (ProcessListControl.EnrichOwners)
        /// could still be mid-WMI-call when the process started tearing down. Cancelling here
        /// gives that thread a chance to notice and stop cleanly before the window is gone.
        /// </summary>
        internal void ShutdownAllProcessLists()
        {
            foreach (var pane in LivePanes())          // Panes.cs
                foreach (var t in pane.Tabs)
                    t.Procs?.Shutdown();
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD OWNERSHIP
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// True while the caret is inside the Task Manager's filter box or its grid. Walked up
        /// the tree rather than tested against one type, the same way EditorHasFocus is
        /// (EditorTabs.cs) - the filter box and the DataGrid are both descendants of
        /// ProcessListControl, not the control itself.
        /// </summary>
        internal bool ProcessListHasFocus
        {
            get
            {
                var d = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
                while (d != null)
                {
                    if (d is ProcessListControl) return true;
                    d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                      ? VisualTreeHelper.GetParent(d)
                      : LogicalTreeHelper.GetParent(d);
                }
                return false;
            }
        }
    }
}
