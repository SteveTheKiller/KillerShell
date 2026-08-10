using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using KillerShell.Models;

// Dragging a tab out of the window entirely, browser-style: past the edge and it lets go,
// becoming its own window. Partial of MainWindow.
//
// Each window is its own OS process (NewWindow.cs), not a second Window in this one - so a torn-
// out tab cannot literally travel across the boundary the way MoveTabToPane moves one between
// panes in the SAME process (PaneDrag.cs). What crosses instead is enough startup state to
// rebuild the tab, the same --new-window / --cwd / --shell mechanism NewWindow.cs and Elevation.cs
// already use to seed a fresh process: a folder tears out exactly like Ctrl+N, a shell tab tears
// out as a FRESH shell in the same folder (the actual running process cannot follow, so this is a
// restart, not a reattachment), a document tab as the same file reopened, and a Processes or
// Event Viewer tab as a fresh one of its own kind. BACKLOG.md has the case for going further
// (real in-process multi-window) if that trade-off ever stops being good enough - flagged there
// rather than assumed.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // ═══════════════════════════════════════════════════════════
        //  GESTURE
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// True once the pointer has cleared the window by a real margin, not just clipped an
        /// edge pixel - the same slack a browser gives you before a tab actually lets go rather
        /// than snapping back to a reorder.
        /// </summary>
        private bool OutsideWindow(System.Windows.Input.MouseEventArgs e)
        {
            const double slack = 40;
            var p = e.GetPosition(this);
            return p.X < -slack || p.X > ActualWidth + slack
                || p.Y < -slack || p.Y > ActualHeight + slack;
        }

        // ═══════════════════════════════════════════════════════════
        //  TEAR OUT
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Close <paramref name="t"/> here and reopen its content in a brand new window. Called
        /// from Tab_DragUp (Tabs.cs) once OutsideWindow says the drop let go outside the frame.
        /// </summary>
        private void TearOutTab(SearchTab t)
        {
            string? flags = BuildRelaunchArgs(t, out string cwd);
            if (flags == null) return;   // BuildRelaunchArgs already set the status (unsaved doc)

            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = "--new-window " + flags,
                WorkingDirectory = Directory.Exists(cwd) ? cwd : (Path.GetDirectoryName(exe) ?? string.Empty),
            };

            try { Process.Start(psi); }
            catch (Exception ex)
            {
                SetTabStatusKey(t, "Str_Status_ElevateFailed", ex.Message);
                return;
            }

            // Only close it here once the new window is actually on its way - a launch failure
            // above returns before this, so a tab is never lost on a tear-out that did not work.
            // If it was the ONLY tab in this pane, leave it: CloseTab would just spawn a fresh
            // blank tab in its place (Tabs.cs), which makes tearing out your one tab read as
            // "clone this window" rather than "move it" - not what the gesture means.
            if (_tabs.Count > 1) CloseTab(t);   // Tabs.cs
        }

        /// <summary>
        /// The --processes / --eventviewer / --performance / --registry / --edit / --shell+--cwd / --cwd
        /// flags (no --new-window) that rebuild <paramref name="t"/> as a fresh tab somewhere
        /// else - a new process's command line (TearOutTab above) or a WM_COPYDATA handoff to
        /// another already-running window (TabHandoff.cs MergeTabIntoWindow), which is the same
        /// idea aimed at an existing window instead of a new one. Null means it could not proceed
        /// - an unsaved or untitled document has nothing safe to hand over - and the tab's own
        /// status line already says why; both callers just bail when they see it.
        /// </summary>
        private string? BuildRelaunchArgs(SearchTab t, out string cwd)
        {
            // CurrentFolder is meaningful on a shell tab too, not just a browse tab - it tracks
            // the live cwd (TerminalTabs.cs DirectoryChanged, "the tab title is where the shell
            // is"). Gating this on IsBrowsing was the bug (2026-08-02): IsBrowsing is
            // false for every shell tab, so a restored or torn-out shell always fell back to
            // Home regardless of where it actually was. Only Home is truly folderless -
            // Processes, Event Viewer and Performance all ignore this out-param entirely (see
            // below) - so any real CurrentFolder wins here now.
            cwd = !string.IsNullOrEmpty(t.CurrentFolder) ? t.CurrentFolder : HomeFolder;

            if (t.IsProcessList) return "--processes";
            if (t.IsEventViewer) return "--eventviewer";
            if (t.IsPerformanceMonitor) return "--performance";
            if (t.IsStorageAnalyzer) return "--storage";
            // Only ever arrives here from an already-elevated window - RegistryEditorTabs.cs has
            // no unelevated entry point at all, so a torn-out or restored Registry tab can only
            // ever have existed inside a process that was already admin, the same reasoning
            // --eventviewer relies on above.
            if (t.IsRegistryEditor) return "--registry";

            if (t.IsTerminal)
            {
                // The running shell cannot follow across a process boundary - a torn-out or
                // merged terminal tab reopens as a FRESH shell in the same folder, not the same
                // session (see the file header). PowerShell rather than whatever it actually
                // was: SearchTab does not remember which of PowerShell/cmd/elevated a shell tab
                // was opened with, only its glyph, which is not enough to tell them apart.
                return "--shell pwsh --cwd \"" + TrimForArg(cwd) + "\"";   // Elevation.cs - TrimForArg
            }

            if (t.Editor != null)
            {
                if (t.Editor.IsUntitled || t.Editor.Dirty)
                {
                    // Nothing safe to hand over: an untitled document has no path to reopen, and
                    // unsaved text would be dropped on the floor mid-drag with no chance to ask
                    // first. Save it here, then drag it again.
                    SetTabStatusKey(t, "Str_Status_TearOutNeedsSave");
                    return null;
                }
                return "--edit \"" + TrimForArg(t.Editor.FilePath) + "\"";
            }

            return "--cwd \"" + TrimForArg(cwd) + "\"";
        }

        /// <summary>
        /// Splits a "--flag value" string the way BuildRelaunchArgs writes it - quoted segments
        /// kept whole, everything else split on spaces. Not a general shell tokenizer (no
        /// escaped quotes, no single quotes) - the only strings this ever sees are ones this
        /// file wrote itself, carried over a WM_COPYDATA handoff instead of real process argv
        /// (TabHandoff.cs ApplyHandoff).
        /// </summary>
        internal static List<string> TokenizeArgs(string s)
        {
            var result = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && s[i] == ' ') i++;
                if (i >= s.Length) break;
                if (s[i] == '"')
                {
                    int j = s.IndexOf('"', i + 1);
                    if (j < 0) j = s.Length;
                    result.Add(s.Substring(i + 1, j - i - 1));
                    i = j + 1;
                }
                else
                {
                    int j = s.IndexOf(' ', i);
                    if (j < 0) j = s.Length;
                    result.Add(s[i..j]);
                    i = j;
                }
            }
            return result;
        }

        /// <summary>
        /// Act on --processes / --eventviewer / --performance / --edit from a tear-out relaunch
        /// (or, for --eventviewer, an elevated one - Elevation.cs RelaunchElevatedEventViewer
        /// hands out the exact same flag Ctrl+F9's Processes relaunch already does for
        /// "--processes", just started with the runas verb instead of plainly; --performance
        /// never goes through an elevated relaunch, since Performance needs none). Called once
        /// the window is up, alongside ApplyStartupShell (Elevation.cs) - same reason: opening
        /// any of these needs the panes to exist first.
        /// </summary>
        internal void ApplyStartupTearOut()
        {
            var args = Environment.GetCommandLineArgs();
            bool acted = false;

            if (Array.Exists(args, a => string.Equals(a, "--processes", StringComparison.OrdinalIgnoreCase)))
            {
                OpenTaskManager();   // ProcessTabs.cs - singleton; fine even as the window's first tab
                acted = true;
            }
            else if (Array.Exists(args, a => string.Equals(a, "--eventviewer", StringComparison.OrdinalIgnoreCase)))
            {
                OpenEventViewer();   // EventViewerTabs.cs - singleton; fine even as the window's first tab
                acted = true;
            }
            else if (Array.Exists(args, a => string.Equals(a, "--performance", StringComparison.OrdinalIgnoreCase)))
            {
                OpenPerformanceMonitor();   // PerformanceTabs.cs - singleton; fine even as the window's first tab
                acted = true;
            }
            else if (Array.Exists(args, a => string.Equals(a, "--storage", StringComparison.OrdinalIgnoreCase)))
            {
                OpenStorageAnalyzer();   // StorageTabs.cs - singleton; fine even as the window's first tab
                acted = true;
            }
            else if (Array.Exists(args, a => string.Equals(a, "--registry", StringComparison.OrdinalIgnoreCase)))
            {
                // Only ever arrives here already elevated - same reasoning as --eventviewer just
                // above: RegistryEditorTabs.cs has no unelevated variant at all.
                OpenRegistryEditor();   // RegistryEditorTabs.cs - singleton; fine even as the window's first tab
                acted = true;
            }
            else
            {
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (!string.Equals(args[i], "--edit", StringComparison.OrdinalIgnoreCase)) continue;

                    string path = args[i + 1];
                    if (File.Exists(path)) { OpenForEditing(path); acted = true; }   // EditorTabs.cs
                    break;
                }
            }

            if (!acted) return;

            // Same rule OpenStartupShell (TerminalTabs.cs) follows: the window seeded itself
            // with a blank Home tab before this ran (Tabs.cs), which is right for an ordinary
            // window and wrong for a torn-out one - it was launched to be THAT ONE tab, and a
            // leftover Home tab beside it is a second thing nobody asked for.
            var keep = Pane.Active;
            var others = new SearchTab[Pane.Tabs.Count];
            Pane.Tabs.CopyTo(others, 0);
            foreach (var t in others)
                if (!ReferenceEquals(t, keep)) FinishCloseTab(t);   // Tabs.cs
        }
    }
}
