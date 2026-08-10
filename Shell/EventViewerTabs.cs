using System.Windows;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Tools;

// Event Viewer tabs, and where they land. Partial of MainWindow.
//
// Same shape as ProcessTabs.cs, and for the same reasons: a new tab in the FOCUSED pane, and a
// SINGLETON rather than a fresh one every time - two Event Viewer tabs would show the same
// machine-wide logs a heartbeat apart, which is not a distinction worth two tabs.
//
// Reached only through Ctrl+F12 (Elevation.cs RelaunchElevatedEventViewer), never a bare key -
// bare F12 is locked family-wide to the About card, and the Security log this tab reads needs
// an elevated token anyway, so there is no honest unelevated entry point to give it.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // E81C (History): reads as "a log of things that already happened", distinct from
        // Processes' E8FD (a live list of what is running now) and the E9D9 spinner earmarked
        // for the future Performance tab. Picked without rendering, same as several other rail
        // glyphs in this app (ViewModeBtn in KillerPDF is the precedent for that caveat) - worth
        // a visual check before shipping.
        private static readonly string EventViewerGlyph = ((char)0xE81C).ToString();

        // ═══════════════════════════════════════════════════════════
        //  OPEN
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Open the Event Viewer tab, or switch to it if one is already open somewhere in the
        /// window. Called from Ctrl+F12 (via ApplyStartupTearOut, once the elevated relaunch has
        /// landed) and from the rail's left click.
        /// </summary>
        internal void OpenEventViewer()
        {
            foreach (var pane in LivePanes())                 // Panes.cs
                foreach (var open in pane.Tabs)
                    if (open.IsEventViewer)
                    {
                        FocusPane(pane);                       // Panes.cs
                        SwitchToTab(open);                     // Tabs.cs
                        return;
                    }

            CaptureTab(_active);                               // Tabs.cs - the outgoing tab keeps its state
            var tab = CreateEventViewerTab();
            ActivateTab(tab);
        }

        /// <summary>
        /// The rail's left click. Unlike the Processes rail button - which can always just switch
        /// to or create its tab, because Processes needs no elevation - this one has to check
        /// first: clicked from an already-elevated window it behaves exactly like
        /// TaskManagerRail_Click, but clicked from an ordinary window it goes through the same
        /// UAC relaunch Ctrl+F12 does (Elevation.cs RelaunchElevatedEventViewer) rather than
        /// opening a tab here that could never read the Security log.
        /// </summary>
        private void EventViewerRail_Click(object sender, RoutedEventArgs e)
        {
            if (IsElevated) OpenEventViewer();          // Elevation.cs - this window already qualifies
            else RelaunchElevatedEventViewer();          // Elevation.cs - hand off to a new elevated one
        }

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        private SearchTab CreateEventViewerTab()
        {
            var tab = CreateTab();                             // Tabs.cs - registers it in this pane

            var events = new EventViewerControl();
            tab.Events     = events;
            tab.TabGlyph   = EventViewerGlyph;
            tab.Title      = Loc("Str_TabTitle_EventViewer");
            tab.IsBrowsing = false;

            return tab;
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIVATION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Swap the pane between its listing and the Event Viewer. Called from ActivateTab, so
        /// it runs on every tab switch in either pane.
        /// </summary>
        /// <remarks>
        /// Runs alongside ApplyProcessListView and quietly re-makes the same decision
        /// ApplyTerminalView and ApplyEditorView already make about ResultsList - see the remark
        /// on ApplyEditorView (EditorTabs.cs) for why that redundancy is deliberate rather than a
        /// leftover.
        /// </remarks>
        private void ApplyEventViewerView(SearchTab t)
        {
            bool events = t.IsEventViewer;

            Pane.EventViewerHost.Visibility = events ? Visibility.Visible : Visibility.Collapsed;

            // MOVED rather than rebuilt: the control owns the picked log/level, the filter text
            // and the grid's own sort/scroll state, and a fresh one per activation would throw
            // all three away on every tab switch, same as it would for a shell, a document or a
            // Processes tab.
            Pane.EventViewerSlot.Content = events ? t.Events : null;
            if (!events) return;

            Pane.ResultsList.Visibility = Visibility.Collapsed;

            // Sorting, view mode and the details header all mean nothing over an event log - the
            // same treatment TerminalTabs.cs gives a shell and ProcessTabs.cs gives Processes.
            ApplyPaneToolbarMode(true);       // TerminalTabs.cs

            var eventsControl = t.Events!;
            // Focus has to wait for the swap to lay out, or it lands on an element that is still
            // collapsed and silently does nothing - same reason the shell, the editor and
            // Processes all defer their own focus call.
            Dispatcher.BeginInvoke(new System.Action(() => eventsControl.FocusFilter()),
                                   System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>Tear an Event Viewer tab down when its tab closes. Called from FinishCloseTab.</summary>
        private void CloseEventViewer(SearchTab t)
        {
            if (t.Events == null) return;
            if (ReferenceEquals(Pane.EventViewerSlot.Content, t.Events)) Pane.EventViewerSlot.Content = null;
            t.Events.Shutdown();
            t.Events = null;
        }

        /// <summary>
        /// Shutdown() every open Event Viewer tab's control without closing the tabs themselves -
        /// called once from Session.cs OnClosing, right as the window really is going away, same
        /// reasoning as ProcessTabs.ShutdownAllProcessLists(): a background log read can still be
        /// mid-call when the process starts tearing down, and cancelling here gives it a chance
        /// to notice and stop cleanly first.
        /// </summary>
        internal void ShutdownAllEventViewers()
        {
            foreach (var pane in LivePanes())          // Panes.cs
                foreach (var t in pane.Tabs)
                    t.Events?.Shutdown();
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD OWNERSHIP
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// True while the caret is inside the Event Viewer's filter box or its grid. Walked up
        /// the tree rather than tested against one type, the same way ProcessListHasFocus is
        /// (ProcessTabs.cs) - the filter box and the DataGrid are both descendants of
        /// EventViewerControl, not the control itself.
        /// </summary>
        internal bool EventViewerHasFocus
        {
            get
            {
                var d = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
                while (d != null)
                {
                    if (d is EventViewerControl) return true;
                    d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                      ? VisualTreeHelper.GetParent(d)
                      : LogicalTreeHelper.GetParent(d);
                }
                return false;
            }
        }
    }
}
