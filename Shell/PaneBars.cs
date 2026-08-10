using KillerShell.Models;

// Which bar a pane wears. Partial of MainWindow.
//
// Five kinds of tab, and only ever one bar up:
//
//   listing        LocationRow    back / forward / up / star / address / view / sort / filter
//   shell          TerminalBar    where the shell is, plus the shell verbs   (TerminalBar.cs)
//   document       the editor bar save / undo / redo / find / go to / wrap / gear (EditorBar.cs)
//   Task Manager   (none)         the filter box lives inside ProcessListControl itself
//   Event Viewer   (none)         the log/level pickers and filter box live inside EventViewerControl
//   Performance    (none)         the gauges and their readouts live inside PerformanceMonitorControl
//   Registry       (none)         the address bar, tree and value grid live inside RegistryEditorControl
//
// They used to be one row for all three, with the listing tools hidden on a shell tab. That was
// fine while a shell was the only other kind, because a shell does have a working directory and
// the address row could just about carry it. A DOCUMENT has nothing the row can say: back and
// forward have no history to walk, up has nowhere to go, the star saves a folder you are not
// looking at, and the view and sort buttons act on a list that is not on screen. Chrome you have
// to read past to reach the two controls you wanted is worse than no chrome. A Task Manager tab
// is the same story again, minus even a bar of its own to carry - there is nothing about a
// process list a location row could say, and its one control (the filter box) already lives
// inside ProcessListControl (Shell/ProcessTabs.cs). An Event Viewer tab is the same story a
// third time, with its own control (Shell/EventViewerControl.cs) carrying its own filter row. A
// Performance tab is the same story a fourth time - there is nothing to filter or navigate over a
// set of live gauges, so it wears no bar either. A Registry Editor tab is the same story a fifth
// time, with its own control (Shell/RegistryEditorControl.cs) carrying its own address bar.
//
// The shell and document bars live inside their own hosts (FilePane.xaml), so they appear and
// disappear with the thing they belong to. The only decision left here is the location row.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Show the location row only on a listing tab. Called from ActivateTab.
        /// </summary>
        /// <remarks>
        /// F10's per-pane hide still wins (MenuBar.cs): a pane whose row the user has put away
        /// keeps it away when they switch back to a folder tab, rather than having it handed
        /// back by a tab switch they did not think of as a request for chrome.
        ///
        /// The animated path is deliberately not used. F10 slides because the row is the thing
        /// you are looking at when you press it; a tab switch replaces the whole pane at once,
        /// and a row sliding shut underneath that reads as lag rather than as motion.
        /// </remarks>
        private void ApplyPaneBars(SearchTab t)
        {
            SetLocationRow(Pane, hidden: !WearsLocationRow(t) || Pane.MenuBarHidden, animate: false);
        }

        /// <summary>
        /// True only for a LISTING tab - the one kind the location row has anything to say
        /// about. Every other kind wears its own bar instead (shell, document) or carries its
        /// controls inside its own control (Task Manager, Event Viewer, Performance, Registry,
        /// Storage).
        /// </summary>
        /// <remarks>
        /// The rule lives here, in one predicate, because it has to be enforced in TWO places
        /// and used to be spelled out in only one. ApplyPaneBars runs on a tab switch; the
        /// Ctrl+F10 menubar toggle (MenuBar.cs) reaches the same row without going through a
        /// tab switch at all, and it had no tab-kind test. Toggling it on a shell tab therefore
        /// slid the folder location row open directly ABOVE the shell's own bar and the tab came
        /// up wearing two identical stacked bars until the next tab switch tidied it away. It
        /// was reachable in one keystroke on any pane whose row was already hidden - which
        /// includes every elevated / --shell startup window, since that path hides the row up
        /// front (TerminalTabs.cs). SetLocationRow now consults this on every call, so no caller
        /// can hand the row to a tab that does not wear one.
        /// </remarks>
        private static bool WearsLocationRow(SearchTab? t)
            => t != null
               && !t.IsTerminal && !t.IsEditor && !t.IsProcessList && !t.IsEventViewer
               && !t.IsPerformanceMonitor && !t.IsRegistryEditor && !t.IsStorageAnalyzer;
    }
}
