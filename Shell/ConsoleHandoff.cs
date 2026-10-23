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
            string program = ProgramPath(console.Info);
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
        /// The program's file, when the console title is its path (it is, unless a shortcut named
        /// it): the tab strip shows that file's own icon. Command Prompt's otherwise.
        /// </summary>
        private static string ProgramPath(HandoffStartupInfo info)
        {
            string title = info.Title.Trim();
            try { if (Path.IsPathRooted(title) && File.Exists(title)) return title; }
            catch (ArgumentException) { }
            return Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

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
