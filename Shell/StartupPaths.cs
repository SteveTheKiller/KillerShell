using System.Collections.Generic;
using System.IO;
using System.Linq;
using KillerShell.Models;

// Paths handed to us on the command line, by Explorer's "Open in KillerShell" verbs or by a
// file association (both registered in Associations.cs). Parsed in App.OnStartup, opened
// here once the window is up.
//
// Deferred to Loaded for the same reason the first-run Home navigation is: navigating a
// folder reveals it in the tree, and the tree's roots do not exist until InitFolderTree has
// run in the constructor.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>Bare (non-flag) command-line arguments, filled by App.OnStartup.</summary>
        internal static readonly List<string> StartupPaths = [];

        /// <summary>
        /// Opens whatever the shell asked us to open: a folder becomes a browse tab, a file
        /// becomes an editor tab. Anything that is neither is skipped rather than reported -
        /// a stale shortcut is not worth a dialog on startup.
        /// </summary>
        private void OpenStartupPaths()
        {
            if (StartupPaths.Count == 0) return;

            // The tab the window opened with. It is still blank when nothing was restored, and
            // the first-run Home navigation in Loaded skips it because these paths are pending.
            var startTab = _active;
            bool first = true;
            foreach (string raw in StartupPaths)
            {
                string path;
                try { path = Path.GetFullPath(raw); } catch { continue; }

                if (Directory.Exists(path))
                {
                    // The first folder reuses the tab that is already open, so a single
                    // "Open in KillerShell" does not leave an empty Home tab behind it.
                    if (!first) ActivateTab(CreateTab());
                    _ = NavigateTo(path);      // Browse.cs
                    first = false;
                }
                else if (File.Exists(path))
                {
                    OpenForEditing(path);      // EditorTabs.cs
                    // A first file takes the blank tab's place, as a first folder reuses it,
                    // so opening one file does not leave an empty tab beside it.
                    if (first && !ReferenceEquals(_active, startTab) && IsBlankTab(startTab))
                        FinishCloseTab(startTab);   // Tabs.cs
                    first = false;
                }
            }

            // Nothing on the command line could be opened: land at Home rather than leave the
            // blank tab sitting on an empty pane.
            if (first && IsBlankTab(startTab)) _ = NavigateTo(HomeFolder);   // Browse.cs

            // Consumed once. A second window opened later from the rail must not re-open
            // whatever the first one was launched with.
            StartupPaths.Clear();
        }

        /// <summary>A tab nothing has been put in yet: no folder, search, document or tool.</summary>
        private static bool IsBlankTab(SearchTab t) =>
            !t.IsBrowsing && string.IsNullOrEmpty(t.CurrentFolder) && t.PipeFiles == null
            && !t.IsTerminal && !t.IsEditor && !t.IsProcessList && !t.IsEventViewer
            && !t.IsPerformanceMonitor && !t.IsRegistryEditor && !t.IsStorageAnalyzer
            && !t.Groups.Any(g => g.Terms.Any(x => !string.IsNullOrWhiteSpace(x.Pattern)));
    }
}
