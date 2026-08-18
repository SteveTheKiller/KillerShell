using System.Windows;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Tools;

// Storage Analyzer tabs, and where they land. Partial of MainWindow.
//
// Same shape as ProcessTabs.cs/EventViewerTabs.cs/PerformanceTabs.cs, and for the same
// reasons: a new tab in the FOCUSED pane, and a SINGLETON - the analyzer's value is the scan
// it holds, and two of them would just split that state across tabs.
//
// Keys: F4 opens it (the address-bar edit that used to sit on bare F4 keeps its Ctrl+L and
// Alt+D aliases - BACKLOG.md reserved F4 for exactly this handover), Ctrl+F4 relaunches
// elevated so a scan can see the folders an ordinary token gets Access Denied on
// (Elevation.cs RelaunchElevatedStorage), mirroring F9/Ctrl+F9 for Processes.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // EDA2 (HardDrive) - the same glyph the folder picker's drive rows already use
        // (FolderPickerDialog.xaml.cs GlyphDrive), so it is proven to render, and it is
        // visually distinct from Processes' E8FD, Event Viewer's E81C and Performance's E9D9.
        private static readonly string StorageGlyph = ((char)0xEDA2).ToString();

        // ═══════════════════════════════════════════════════════════
        //  OPEN
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Open the Storage Analyzer tab, or switch to it if one is already open somewhere in
        /// the window. The rail's click, plain F4, and the --storage startup flag.
        /// </summary>
        internal void OpenStorageAnalyzer()
        {
            foreach (var pane in LivePanes())                 // Panes.cs
                foreach (var open in pane.Tabs)
                    if (open.IsStorageAnalyzer)
                    {
                        FocusPane(pane);                       // Panes.cs
                        SwitchToTab(open);                     // Tabs.cs
                        return;
                    }

            CaptureTab(_active);                               // Tabs.cs - the outgoing tab keeps its state
            var tab = CreateStorageAnalyzerTab();
            ActivateTab(tab);
        }

        private void StorageRail_Click(object sender, RoutedEventArgs e) => OpenStorageAnalyzer();

        /// <summary>
        /// Open the Storage Analyzer aimed at one folder and start scanning it. The
        /// "Analyze storage" verb every folder surface offers - the listing, the tree and the
        /// saved places.
        /// </summary>
        /// <remarks>
        /// Reuses the singleton rather than opening a second tab: two analyzers would each hold
        /// a full scan tree, and the tab is a place you go rather than a document you keep. The
        /// scan starts immediately, because picking "analyze this folder" has already said
        /// which folder and waiting for a second click on Scan would be asking twice.
        /// </remarks>
        internal void AnalyzeFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) return;

            OpenStorageAnalyzer();
            _active?.Storage?.ScanFolder(folder);
        }

        // The rail button's right-click pair, mirroring RailProcessesOpen/Admin: plain open and
        // the elevated relaunch, same two actions F4 / Ctrl+F4 drive.
        private void RailStorageOpen_Click(object sender, RoutedEventArgs e)  => OpenStorageAnalyzer();
        private void RailStorageAdmin_Click(object sender, RoutedEventArgs e) => RelaunchElevatedStorage();   // Elevation.cs

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        private SearchTab CreateStorageAnalyzerTab()
        {
            var tab = CreateTab();                             // Tabs.cs - registers it in this pane

            // Seeded with the folder the user was just looking at - "scan where I am" is the
            // common case; the target box is right there for anything else.
            var storage = new StorageAnalyzerControl(_active?.CurrentFolder)
            {
                // A dir picked from the map opens as an ordinary browse tab, so "what IS all
                // this" flows straight into "go deal with it". Same fresh-tab-then-navigate
                // shape OpenBookmark uses when the focused tab cannot be navigated - and a
                // Storage tab never can be.
                OpenFolderInNewTab = path => { ActivateTab(CreateTab()); _ = NavigateTo(path); },
            };
            storage.ExportRequested = () => ExportStorageAnalyzer(tab, storage);
            tab.Storage    = storage;
            tab.TabGlyph   = StorageGlyph;
            tab.Title      = Loc("Str_TabTitle_Storage");
            tab.IsBrowsing = false;

            // Scan progress, the finished summary and errors ride the tab's own status line,
            // so they land in the window's STATUS BAR whenever this tab is active - not in an
            // inline readout that resized the target box with every update.
            storage.ReportStatus = msg => SetTabStatus(tab, msg);   // MainWindow.xaml.cs
            SetTabStatus(tab, Loc("Str_Storage_Prompt"));

            return tab;
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIVATION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Swap the pane between its listing and the Storage Analyzer. Called from ActivateTab,
        /// so it runs on every tab switch in either pane - same shape and same deliberate
        /// redundancy as ApplyPerformanceMonitorView (see the remark there).
        /// </summary>
        private void ApplyStorageAnalyzerView(SearchTab t)
        {
            bool storage = t.IsStorageAnalyzer;

            Pane.StorageHost.Visibility = storage ? Visibility.Visible : Visibility.Collapsed;

            // MOVED rather than rebuilt: the control IS the scan result, and a rebuild per
            // activation would throw a whole drive walk away on every tab switch.
            Pane.StorageSlot.Content = storage ? t.Storage : null;
            if (!storage) return;

            Pane.ResultsList.Visibility = Visibility.Collapsed;
            ApplyPaneToolbarMode(true);       // TerminalTabs.cs - no sort/view/details over a treemap
        }

        /// <summary>Tear a Storage Analyzer tab down when its tab closes. Called from
        /// FinishCloseTab.</summary>
        private void CloseStorageAnalyzer(SearchTab t)
        {
            if (t.Storage == null) return;
            if (ReferenceEquals(Pane.StorageSlot.Content, t.Storage)) Pane.StorageSlot.Content = null;
            t.Storage.Shutdown();
            t.Storage = null;
        }

        /// <summary>
        /// Shutdown() every open Storage Analyzer's control without closing the tabs
        /// themselves - called once from Session.cs OnClosing, same as the other tool tabs: a
        /// scan's worker threads are background threads, but canceling them here lets a
        /// mid-walk FindFirstFile handle close cleanly instead of being torn down.
        /// </summary>
        internal void ShutdownAllStorageAnalyzers()
        {
            foreach (var pane in LivePanes())          // Panes.cs
                foreach (var t in pane.Tabs)
                    t.Storage?.Shutdown();
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD OWNERSHIP
        // ═══════════════════════════════════════════════════════════
        /// <summary>True while focus is inside the Storage Analyzer control - same tree walk
        /// as ProcessListHasFocus/PerformanceMonitorHasFocus.</summary>
        internal bool StorageAnalyzerHasFocus
        {
            get
            {
                var d = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
                while (d != null)
                {
                    if (d is StorageAnalyzerControl) return true;
                    d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                      ? VisualTreeHelper.GetParent(d)
                      : LogicalTreeHelper.GetParent(d);
                }
                return false;
            }
        }
    }
}
