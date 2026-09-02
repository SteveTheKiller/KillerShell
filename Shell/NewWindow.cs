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
            OpenNewWindowInternal(null);
        }

        /// <remarks>
        /// This took a `bool elevate` that nothing in the method ever read: no `Verb = "runas"` was
        /// ever set, so a caller asking for an elevated window would have got an ordinary one and
        /// no warning - and the catch below still reports it as `Str_Status_ElevateFailed`, which
        /// is what gives the intent away. Only one caller exists and it passes false, so nothing
        /// shipped broken, but a flag that silently does nothing is a trap. It is gone; if an
        /// elevated new window is ever wanted, it needs the real relaunch Elevation.cs already
        /// does, not a parameter.
        /// </remarks>
        private void OpenNewWindowInternal(string? folder)
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
