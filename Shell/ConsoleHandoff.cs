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

            // Command Prompt's profile, whatever the program is: it is the classic console's look
            // and a generic console glyph, and it never tries to start a shell of its own.
            var profile = TerminalProfile.Cmd(elevated: IsElevated);
            var tab = CreateTerminalTabIn(Pane, profile, HomeFolder, console.Connection,
                                          ConsoleTitle(console.Info, profile));
            if (tab == null) return;

            FocusPane(Pane);       // Panes.cs
            ActivateTab(tab);      // Tabs.cs
            StripToShell();        // TerminalTabs.cs
        }

        /// <summary>
        /// The console title is a shortcut's name or the program's full path. A tab has room for
        /// a name, so a path is cut to its file name, as Task Manager shows it.
        /// </summary>
        internal static string ConsoleTitle(HandoffStartupInfo info, TerminalProfile fallback)
        {
            string title = info.Title.Trim();
            if (title.Length == 0) return fallback.Name;
            if (title.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return title;
            return Path.IsPathRooted(title) ? Path.GetFileName(title) : title;
        }
    }
}
