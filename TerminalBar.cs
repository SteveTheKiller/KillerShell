using System.Linq;
using System.Windows;
using System.Windows.Input;
using KillerShell.Models;

// The shell bar: the strip across the top of a terminal tab. Partial of MainWindow.
//
// It replaces the folder location row on a shell tab (PaneBars.cs). The row was never much use
// there: back and forward walk a folder history a pty knows nothing about, up has nowhere to go,
// and typing a path into the address box navigates the TAB rather than cd-ing the shell - which
// is the kind of near-miss that is worse than a missing control.
//
// What is left is the one thing that is genuinely live: where the shell currently is. The buffer
// already tracks it from OSC 7 (TerminalTabs wires DirectoryChanged), so the readout is free and
// always right, including after a cd three levels deep in a script.
//
// The buttons are the menu rows a hand reaches for often enough not to want a right-click first.
// Everything else - copy, paste, select all, edit prompt, reset prompt, close - stays in the
// menu, because a bar that carries every command is a bar that starts shedding them on a split
// window (Terminal/TerminalMenu.cs).
namespace KillerShell
{
    public partial class MainWindow
    {
        /// <summary>Repaint the shell bar for <paramref name="t"/>, in the pane showing it.</summary>
        private void SyncTerminalBar(SearchTab t)
        {
            if (t.Term == null) return;

            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.TerminalSlot.Content, t.Term));
            if (pane == null) return;

            pane.TermCwdText.Text = string.IsNullOrEmpty(t.CurrentFolder)
                ? Loc("Str_Scope_Empty")
                : t.CurrentFolder;
        }

        /// <summary>
        /// Clicking the path opens it as a folder tab - the reverse of "open a terminal here".
        /// </summary>
        /// <remarks>
        /// Same rule as the menu row it mirrors: after a few minutes of cd-ing around, getting
        /// the listing to follow otherwise means copying the path and pasting it into the other
        /// pane's address bar.
        /// </remarks>
        internal void TermCwd_Click(object sender, MouseButtonEventArgs e)
            => OpenFolderTabLeft(_active.CurrentFolder);      // TerminalTabs.cs

        internal void TermNew_Click(object sender, RoutedEventArgs e)
            => OpenShell(Terminal.TerminalProfile.PowerShell(), _active.CurrentFolder);

        internal void TermAdmin_Click(object sender, RoutedEventArgs e)
            => OpenShell(Terminal.TerminalProfile.PowerShell(elevated: true), _active.CurrentFolder);

        internal void TermFolder_Click(object sender, RoutedEventArgs e)
            => OpenFolderTabLeft(_active.CurrentFolder);

        /// <summary>
        /// Clear the screen by asking the SHELL to, not by wiping our own buffer.
        /// </summary>
        /// <remarks>
        /// Same reasoning as the menu row: emptying the buffer ourselves would leave the shell
        /// believing it had already drawn a prompt, so the next keystroke would paint over
        /// nothing. cls means the same thing in pwsh, powershell and cmd.
        /// </remarks>
        internal void TermClear_Click(object sender, RoutedEventArgs e)
        {
            var term = _active.Term;
            if (term == null) return;
            term.Send("cls\r");
            term.Focus();
        }

        internal void TermFonts_Click(object sender, RoutedEventArgs e)
            => FontsRow_Click(this, new RoutedEventArgs());   // Fonts.cs
    }
}
