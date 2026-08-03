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
namespace KillerShell.Shell
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
        /// Click the shell's cwd to edit it in place - type a folder and press Enter to cd the
        /// LIVE shell there, not just the tab's own bookkeeping.
        /// </summary>
        /// <remarks>
        /// This used to open the path as a folder tab instead, the same job TermFolderBtn (the
        /// folder icon two buttons over) already does on its own - a real control duplicated by
        /// the text next to it. What replaced it is the thing a shell bar's address really ought
        /// to do: an ordinary address box that used to sit here for every kind of tab navigated
        /// the TAB rather than cd-ing the shell, which is exactly the near-miss TerminalBar.cs's
        /// header comment says this bar exists to avoid - so this is scoped narrowly to sending
        /// one `cd` to the shell, never touching CurrentFolder/RootPath itself. Those still only
        /// ever change from the shell's own OSC 7 report (TerminalTabs.cs DirectoryChanged),
        /// which is what actually fires once the cd below lands.
        /// </remarks>
        internal void TermCwd_Click(object sender, MouseButtonEventArgs e)
        {
            var t = _active;
            if (t.Term == null) return;
            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.TerminalSlot.Content, t.Term));
            if (pane == null) return;

            pane.TermCwdBox.Text = t.CurrentFolder;
            pane.TermCwdText.Visibility = Visibility.Collapsed;
            pane.TermCwdBox.Visibility = Visibility.Visible;
            pane.TermCwdBox.Focus();
            pane.TermCwdBox.SelectAll();
        }

        private void EndEditTermCwd(FilePane pane)
        {
            pane.TermCwdBox.Visibility = Visibility.Collapsed;
            pane.TermCwdText.Visibility = Visibility.Visible;
        }

        /// <summary>Enter commits (cd's the shell), Escape cancels back to the live readout.</summary>
        internal void TermCwdBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox box) return;
            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.TermCwdBox, box));
            if (pane == null) return;

            if (e.Key == Key.Escape)
            {
                EndEditTermCwd(pane);
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            var t = pane.Active;
            if (t?.Term == null) { EndEditTermCwd(pane); return; }

            string path = box.Text.Trim();
            if (!System.IO.Directory.Exists(path))
            {
                // Stays in edit mode rather than reverting - a typo is worth the chance to fix,
                // not a round trip back through click-to-edit.
                SetTabStatusKey(t, "Str_Status_TermCwdNotFound", path);
                return;
            }

            // cd, not Set-Location - the one alias every shell this app can open understands
            // (pwsh, Windows PowerShell, cmd), so this never has to know which one it is talking
            // to. TrimForArg (Elevation.cs) so a drive root ("C:\") does not get trimmed down to
            // the drive-relative "C:" - the same trap Up-from-a-drive-root already had to dodge.
            t.Term.Send("cd \"" + TrimForArg(path) + "\"\r");
            t.Term.Focus();
            EndEditTermCwd(pane);
        }

        /// <summary>Clicking away without pressing Enter cancels rather than commits.</summary>
        internal void TermCwdBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox box) return;
            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.TermCwdBox, box));
            if (pane != null) EndEditTermCwd(pane);
        }

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
