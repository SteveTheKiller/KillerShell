using System.Windows;
using System.Windows.Media;
using KillerShell.Models;

// Registry Editor tabs, and where they land. Partial of MainWindow.
//
// Same shape as EventViewerTabs.cs, and for the same reasons: a new tab in the FOCUSED pane, and
// a SINGLETON rather than a fresh one every time - two Registry Editor tabs would show the same
// machine-wide registry a heartbeat apart, which is not a distinction worth two tabs.
//
// Reached only through Ctrl+F11 (Elevation.cs RelaunchElevatedRegistryEditor), never a bare key -
// bare F11 is locked to the Performance tab (PerformanceTabs.cs), and most of the registry that
// matters refuses writes - and a lot of it refuses reads - to a process that is not elevated, so
// there is no honest, useful unelevated entry point to give this tab either.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // E71D (List) - matches MainWindow.xaml's RegistryEditorRailBtn. Went through E945 (a
        // lightning bolt, not the intended database-cylinder glyph) and E713 (Settings gear,
        // too generic) before landing here - a tree/list shape reads closer to a registry hive
        // browser and pairs cleanly against Processes' E8FD. Kept in this one constant so the
        // rail button and the tab strip can't drift apart the way they did before.
        // E74C (OEM) - a four-pane window with a corner mark, which reads as the registry's
        // hive/key structure. E71D (List) went to Event Viewer, whose tab genuinely is a list of
        // log lines (Steve, 2026-08-08). The rail button in MainWindow.xaml carries the same
        // glyph; change both together or they drift, which is what this constant exists to stop.
        private static readonly string RegistryEditorGlyph = ((char)0xE74C).ToString();

        // ═══════════════════════════════════════════════════════════
        //  OPEN
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Open the Registry Editor tab, or switch to it if one is already open somewhere in the
        /// window. Called from Ctrl+F11 (via ApplyStartupTearOut, once the elevated relaunch has
        /// landed) and from the rail's left click.
        /// </summary>
        internal void OpenRegistryEditor()
        {
            foreach (var pane in LivePanes())                 // Panes.cs
                foreach (var open in pane.Tabs)
                    if (open.IsRegistryEditor)
                    {
                        FocusPane(pane);                       // Panes.cs
                        SwitchToTab(open);                     // Tabs.cs
                        return;
                    }

            CaptureTab(_active);                               // Tabs.cs - the outgoing tab keeps its state
            var tab = CreateRegistryEditorTab();
            ActivateTab(tab);
        }

        /// <summary>
        /// The rail's left click. Same shape as EventViewerRail_Click: clicked from an already-
        /// elevated window it behaves exactly like the plain open, but clicked from an ordinary
        /// window it goes through the same UAC relaunch Ctrl+F11 does (Elevation.cs
        /// RelaunchElevatedRegistryEditor) rather than opening a tab here that could never write
        /// most of what a registry editor exists to edit.
        /// </summary>
        private void RegistryEditorRail_Click(object sender, RoutedEventArgs e)
        {
            if (IsElevated) OpenRegistryEditor();          // Elevation.cs - this window already qualifies
            else RelaunchElevatedRegistryEditor();          // Elevation.cs - hand off to a new elevated one
        }

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        private SearchTab CreateRegistryEditorTab()
        {
            var tab = CreateTab();                             // Tabs.cs - registers it in this pane

            var registry = new RegistryEditorControl();
            tab.Registry   = registry;
            tab.TabGlyph   = RegistryEditorGlyph;
            tab.Title      = Loc("Str_TabTitle_RegistryEditor");
            tab.IsBrowsing = false;

            return tab;
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIVATION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Swap the pane between its listing and the Registry Editor. Called from ActivateTab, so
        /// it runs on every tab switch in either pane.
        /// </summary>
        /// <remarks>
        /// Runs alongside ApplyProcessListView/ApplyEventViewerView/ApplyPerformanceMonitorView and
        /// quietly re-makes the same decision ApplyTerminalView and ApplyEditorView already make
        /// about ResultsList - see the remark on ApplyEditorView (EditorTabs.cs) for why that
        /// redundancy is deliberate rather than a leftover.
        /// </remarks>
        private void ApplyRegistryEditorView(SearchTab t)
        {
            bool registry = t.IsRegistryEditor;

            Pane.RegistryEditorHost.Visibility = registry ? Visibility.Visible : Visibility.Collapsed;

            // MOVED rather than rebuilt: the control owns the loaded tree (which keys are
            // expanded), the currently selected key's value list and the grid's own sort/scroll
            // state, and a fresh one per activation would throw all three away on every tab
            // switch, same as it would for a shell, a document, Processes, Event Viewer or
            // Performance.
            Pane.RegistryEditorSlot.Content = registry ? t.Registry : null;
            if (!registry) return;

            Pane.ResultsList.Visibility = Visibility.Collapsed;

            // Sorting, view mode and the details header all mean nothing over a registry tree -
            // the same treatment every other non-listing tab kind gets.
            ApplyPaneToolbarMode(true);       // TerminalTabs.cs
        }

        /// <summary>Tear a Registry Editor tab down when its tab closes. Called from
        /// FinishCloseTab.</summary>
        private void CloseRegistryEditor(SearchTab t)
        {
            if (t.Registry == null) return;
            if (ReferenceEquals(Pane.RegistryEditorSlot.Content, t.Registry)) Pane.RegistryEditorSlot.Content = null;
            t.Registry.Shutdown();
            t.Registry = null;
        }

        /// <summary>
        /// Shutdown() every open Registry Editor tab's control without closing the tabs
        /// themselves - called once from Session.cs OnClosing, right as the window really is
        /// going away, same pattern ShutdownAllEventViewers/ShutdownAllPerformanceMonitors follow.
        /// Unlike those two there is no background load or timer here - every registry read this
        /// control does is a fast local call, and Ctrl+F only ever walks the already-loaded tree
        /// (RegistryEditorControl.cs) - but the status-clear timer still needs stopping so it
        /// cannot tick after the control's own Dispatcher is gone.
        /// </summary>
        internal void ShutdownAllRegistryEditors()
        {
            foreach (var pane in LivePanes())          // Panes.cs
                foreach (var t in pane.Tabs)
                    t.Registry?.Shutdown();
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD OWNERSHIP
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// True while focus is inside the Registry Editor control - its address bar, its find box,
        /// its tree or its value grid. Walked up the tree rather than tested against one type, the
        /// same way ProcessListHasFocus/EventViewerHasFocus are.
        /// </summary>
        internal bool RegistryEditorHasFocus
        {
            get
            {
                var d = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
                while (d != null)
                {
                    if (d is RegistryEditorControl) return true;
                    d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                      ? VisualTreeHelper.GetParent(d)
                      : LogicalTreeHelper.GetParent(d);
                }
                return false;
            }
        }
    }
}
