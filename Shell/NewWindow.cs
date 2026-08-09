using System;
using System.Diagnostics;

// A second KillerShell window. Partial of MainWindow.
//
// A new PROCESS, not a second Window in this one. The obvious in-process version shares more
// than it looks: the theme and settings store, the bundled-module unpack flag, the terminal font
// statics - and, worst of them, the session write on exit, where two windows in one process
// would race to save whichever set of tabs quit last over the other's.
//
// A separate process gets the isolation for free and is what Explorer does anyway. It costs a
// cold start, which is the price of not having to audit every static in the app.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Open another window on the folder this one is showing. Explorer's Ctrl+N.
        /// </summary>
        /// <remarks>
        /// The folder is passed with the same --cwd the elevated relaunch uses (Elevation.cs),
        /// so a new window lands where you were rather than at home. Without it the first thing
        /// you would do every time is navigate back to the folder you just left.
        /// </remarks>
        internal void OpenNewWindow()
        {
            OpenNewWindowInternal(null, elevate: false);
        }

        /// <summary>
        /// Open a new window explicitly unelevated, even if called from an elevated process.
        /// Used when F8 (non-elevated shell) is pressed in an admin-only window.
        /// </summary>
        internal static void OpenUnelevated(string? folder = null)
        {
            // From an elevated process, we need to explicitly drop privileges. runas with /user
            // is the standard way, but it prompts. Instead, use Explorer.exe as a launcher - it
            // runs unelevated by default even when called from an elevated process, because
            // Explorer is designed to be a shell that can launch things at any privilege level.
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            // KNOWN BROKEN, do not trust this path (Steve, 2026-08-08). "explorer.exe <file>"
            // launches ONE file and takes no arguments for it: Explorer parses each remaining
            // token as something to open, fails, and opens a folder window per token instead. So
            // this does not start KillerShell with --new-window/--cwd at all, it just throws up
            // Explorer windows. It only ever ran by accident - an elevated window restoring the
            // session and asking for a non-elevated shell per tab - which is fixed at the source
            // in MainWindow.xaml.cs (an elevated window no longer restores). Pressing F8 in an
            // admin window still reaches here and still misbehaves; dropping privileges properly
            // needs the shell's own ShellExecute via IShellDispatch2, not this.
            var psi = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = false,
                Arguments = "\"" + exe + "\" --new-window" +
                           (!string.IsNullOrEmpty(folder) ? " --cwd \"" + folder + "\"" : ""),
                WorkingDirectory = string.IsNullOrEmpty(folder) ?
                                  System.IO.Path.GetDirectoryName(exe) ?? string.Empty : folder,
            };

            try { Process.Start(psi); }
            catch { /* failed to launch; nothing we can do from here */ }
        }

        private void OpenNewWindowInternal(string? folder, bool elevate)
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            string? here = _active?.IsBrowsing == true ? _active.CurrentFolder : (folder ?? null);

            // This PC is a sentinel rather than a directory, so it cannot be a working directory
            // and is not worth passing - the new window opens at home instead.
            bool real = !string.IsNullOrEmpty(here) && !IsThisPc(here)     // Browse.cs
                        && System.IO.Directory.Exists(here);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                // --new-window suppresses the session restore in the new process. Without it a
                // second window came up carrying every tab from the last session, which is not
                // what Ctrl+N means anywhere: a new window is one tab, here or at home.
                Arguments = real ? "--new-window --cwd \"" + TrimForArg(here!) + "\"" : "--new-window",
                WorkingDirectory = real ? here! : System.IO.Path.GetDirectoryName(exe) ?? string.Empty,
            };

            try { Process.Start(psi); }
            catch (Exception ex)
            {
                if (_active != null) SetTabStatusKey(_active, "Str_Status_ElevateFailed", ex.Message);
            }
        }
    }
}
