using System;
using System.IO;
using KillerShell.Terminal;

// The window a console program opens in when KillerShell is the default terminal
// (Terminal/TerminalHandoff.cs, DefaultTerminal.cs). Partial of MainWindow.
//
// Windows starts one KillerShell per console program, and this is that process's only window.
// It is built like the admin window an elevated relaunch opens - one shell tab, no menubar, no
// sidebar, wide enough for 90 columns - and for the same reason it is a task you finish, not the
// window you live in: it neither restores nor saves the session, skips the quit prompt and the
// update offer, and never hides to the tray.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>The console the next window is for. Set by App before it builds the window.</summary>
        internal static (HandoffConnection Connection, HandoffStartupInfo Info)? PendingConsole { get; set; }

        private readonly (HandoffConnection Connection, HandoffStartupInfo Info)? _console = TakePendingConsole();

        private static (HandoffConnection, HandoffStartupInfo)? TakePendingConsole()
        {
            var pending = PendingConsole;
            PendingConsole = null;
            return pending;
        }

        /// <summary>True for the window of a handed-off console program.</summary>
        internal bool IsConsoleWindow => _console != null;

        /// <summary>Puts the handed-off program in this window's one tab. Runs once, on Loaded.</summary>
        private void ApplyConsoleHandoff()
        {
            if (_console is not { } console) return;

            // The default profile's look, so a program started from Explorer looks like the shells
            // opened here; never its command, because the program is already running.
            string program = ProgramPath(console.Info, console.Connection.ProcessId);
            var profile = TerminalProfile.Handoff(TerminalProfileStore.Default(), IsElevated, program);
            var tab = CreateTerminalTabIn(Pane, profile, HomeFolder, console.Connection,
                                          ConsoleTitle(console.Info));
            if (tab == null) return;

            // As a console window does: a program that finishes cleanly takes its window with it,
            // and one that fails leaves its output and exit code on screen. Tabs opened here since
            // are the user's, so with any of them open only the program's own tab goes.
            tab.Term!.Exited += code => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (code != 0) return;
                if (LeftPane.Tabs.Count + RightPane.Tabs.Count <= 1) Close();
                else CloseTab(tab);   // Tabs.cs
            }));

            FocusPane(Pane);       // Panes.cs
            ActivateTab(tab);      // Tabs.cs
            StripToShell();        // TerminalTabs.cs
        }

        /// <summary>
        /// The program's file: the one its process runs, which a Start menu shortcut's title
        /// ("Windows PowerShell") cannot tell apart from Command Prompt. The tab strip shows its
        /// icon, and Clear and the folder box speak its language (TerminalProfile.Handoff). The
        /// console title when it is a path, and Command Prompt's as the last resort.
        /// </summary>
        private static string ProgramPath(HandoffStartupInfo info, int processId)
        {
            if (ImagePath(processId) is { } image) return image;
            string title = info.Title.Trim();
            try { if (Path.IsPathRooted(title) && File.Exists(title)) return title; }
            catch (ArgumentException) { }
            return Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

        /// <summary>The executable a running process was started from; null when it cannot be read.</summary>
        internal static string? ImagePath(int processId)
        {
            if (processId <= 0) return null;
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return null;
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally { CloseHandle(process); }
        }

        private const int ProcessQueryLimitedInformation = 0x1000;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// The console title is a shortcut's name or the program's full path. A tab has room for
        /// a name, so a path is cut to its file name, as Task Manager shows it.
        /// </summary>
        internal static string ConsoleTitle(HandoffStartupInfo info)
        {
            string title = info.Title.Trim();
            if (title.Length == 0) return LocStatic("Str_Ks_ScopeTerminal");   // "Terminal"
            if (title.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return title;
            return Path.IsPathRooted(title) ? Path.GetFileName(title) : title;
        }
    }
}
