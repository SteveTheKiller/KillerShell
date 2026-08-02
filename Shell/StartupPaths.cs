using System.Collections.Generic;
using System.IO;

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
        internal static readonly List<string> StartupPaths = new();

        /// <summary>
        /// Opens whatever the shell asked us to open: a folder becomes a browse tab, a file
        /// becomes an editor tab. Anything that is neither is skipped rather than reported -
        /// a stale shortcut is not worth a dialog on startup.
        /// </summary>
        private void OpenStartupPaths()
        {
            if (StartupPaths.Count == 0) return;

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
                    first = false;
                }
            }

            // Consumed once. A second window opened later from the rail must not re-open
            // whatever the first one was launched with.
            StartupPaths.Clear();
        }
    }
}
