using System.Windows;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Tools;

// Performance Monitor tabs, and where they land. Partial of MainWindow.
//
// Same shape as ProcessTabs.cs/EventViewerTabs.cs, and for the same reasons: a new tab in the
// FOCUSED pane, and a SINGLETON rather than a fresh one every time - two Performance tabs would
// show the same machine-wide gauges a heartbeat apart, which is not a distinction worth two tabs.
//
// Unlike Event Viewer, this one needs NO elevation: CPU/RAM/network throughput and disk activity
// are all read through System.Diagnostics.PerformanceCounter, which any ordinary user account can
// query, and the one-time hardware inventory (CPU model, installed RAM, GPU model, adapter names)
// is read through the same unauthenticated local WMI classes Processes already uses
// (Win32_Processor/Win32_VideoController/Win32_ComputerSystem/Win32_NetworkAdapter). So there is
// no elevated variant here and no Ctrl+F11 - F11 is the only entry point. BACKLOG.md's
// reservation note assumed elevation might be needed before this tab was designed; it was not,
// and the note has been updated to say so.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // E9D9 (Processing - a spinner): earmarked for this tab specifically back when the
        // Processes glyph (E8FD) was picked, so a live-updating gauge would read as visually
        // distinct from a static list the moment both sat on the rail together (ProcessTabs.cs).
        // Distinct again from Event Viewer's E81C (a log of things that already happened).
        private static readonly string PerformanceGlyph = ((char)0xE9D9).ToString();

        // ═══════════════════════════════════════════════════════════
        //  OPEN
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Open the Performance Monitor tab, or switch to it if one is already open somewhere in
        /// the window. The rail's left click, and plain F11.
        /// </summary>
        internal void OpenPerformanceMonitor()
        {
            foreach (var pane in LivePanes())                 // Panes.cs
                foreach (var open in pane.Tabs)
                    if (open.IsPerformanceMonitor)
                    {
                        FocusPane(pane);                       // Panes.cs
                        SwitchToTab(open);                     // Tabs.cs
                        return;
                    }

            CaptureTab(_active);                               // Tabs.cs - the outgoing tab keeps its state
            var tab = CreatePerformanceMonitorTab();
            ActivateTab(tab);
        }

        private void PerformanceRail_Click(object sender, RoutedEventArgs e) => OpenPerformanceMonitor();

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        private SearchTab CreatePerformanceMonitorTab()
        {
            var tab = CreateTab();                             // Tabs.cs - registers it in this pane

            var perf = new PerformanceMonitorControl();
            tab.Perf       = perf;
            tab.TabGlyph   = PerformanceGlyph;
            tab.Title      = Loc("Str_TabTitle_Performance");
            tab.IsBrowsing = false;

            return tab;
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIVATION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Swap the pane between its listing and the Performance Monitor. Called from ActivateTab,
        /// so it runs on every tab switch in either pane.
        /// </summary>
        /// <remarks>
        /// Runs alongside ApplyProcessListView/ApplyEventViewerView and quietly re-makes the same
        /// decision ApplyTerminalView and ApplyEditorView already make about ResultsList - see the
        /// remark on ApplyEditorView (EditorTabs.cs) for why that redundancy is deliberate rather
        /// than a leftover.
        /// </remarks>
        private void ApplyPerformanceMonitorView(SearchTab t)
        {
            bool perf = t.IsPerformanceMonitor;

            Pane.PerformanceHost.Visibility = perf ? Visibility.Visible : Visibility.Collapsed;

            // MOVED rather than rebuilt: the control owns a live refresh timer and every metric's
            // sparkline history, and a fresh one per activation would throw both away on every
            // tab switch, same as it would for a shell, a document, Processes or Event Viewer. It
            // also re-runs the one-time hardware inventory query for nothing.
            Pane.PerformanceSlot.Content = perf ? t.Perf : null;
            if (!perf) return;

            Pane.ResultsList.Visibility = Visibility.Collapsed;

            // Sorting, view mode and the details header all mean nothing over a gauge readout -
            // the same treatment every other non-listing tab kind gets.
            ApplyPaneToolbarMode(true);       // TerminalTabs.cs
        }

        /// <summary>Tear a Performance Monitor tab down when its tab closes. Called from
        /// FinishCloseTab.</summary>
        private void ClosePerformanceMonitor(SearchTab t)
        {
            if (t.Perf == null) return;
            if (ReferenceEquals(Pane.PerformanceSlot.Content, t.Perf)) Pane.PerformanceSlot.Content = null;
            t.Perf.Shutdown();
            t.Perf = null;
        }

        /// <summary>
        /// Shutdown() every open Performance Monitor tab's control without closing the tabs
        /// themselves - called once from Session.cs OnClosing, right as the window really is
        /// going away, same reasoning as ShutdownAllProcessLists/ShutdownAllEventViewers: a
        /// PerformanceCounter can still be mid-sample when the process starts tearing down, and
        /// stopping the timer here first gives it a chance to notice and stop cleanly.
        /// </summary>
        internal void ShutdownAllPerformanceMonitors()
        {
            foreach (var pane in LivePanes())          // Panes.cs
                foreach (var t in pane.Tabs)
                    t.Perf?.Shutdown();
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD OWNERSHIP
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// True while focus is inside the Performance Monitor control. Walked up the tree rather
        /// than tested against one type, the same way ProcessListHasFocus/EventViewerHasFocus are.
        /// </summary>
        internal bool PerformanceMonitorHasFocus
        {
            get
            {
                var d = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
                while (d != null)
                {
                    if (d is PerformanceMonitorControl) return true;
                    d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                      ? VisualTreeHelper.GetParent(d)
                      : LogicalTreeHelper.GetParent(d);
                }
                return false;
            }
        }
    }
}
