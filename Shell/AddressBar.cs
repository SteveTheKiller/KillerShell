using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerShell.Shell
{
    // The location row, as an address bar. Partial of MainWindow.
    //
    // This row used to be search's "which folder am I searching" control, so its whole job was
    // to open a folder picker and its empty state read "no folder selected". Now that a tab is
    // always somewhere, it is the address bar: it shows where you are, and clicking it - or
    // Ctrl+L, or Alt+D - lets you type a path.
    //
    // The picker is still there behind Ctrl+O, because browsing for a folder is a different act
    // from typing a path you already know.
    public partial class MainWindow
    {
        // Where new tabs start, and what Alt+Home would go to. The user profile unless it has
        // been pointed somewhere else.
        internal static string HomeFolder { get; private set; } =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Where a new tab and a newly opened second pane land. --demo browses a fabricated profile
        // (DemoFileSystem.cs), so there it is that profile and a tab is never named for the real
        // account running the capture. Terminals and other real processes keep HomeFolder.
        internal static string NewTabFolder => DemoMode ? @"C:\Users\Demo" : HomeFolder;

        private static void InitHomeFolder()
        {
            string saved = Services.ThemeManager.GetSetting("HomeFolder") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved)) HomeFolder = saved;
        }

        // ── Entering edit mode ───────────────────────────────────
        internal void ScopeBar_Click(object sender, MouseButtonEventArgs e)
        {
            if (Pane.AddressBox.Visibility == Visibility.Visible) return;
            BeginEditAddress();
            e.Handled = true;
        }

        internal void BeginEditAddress()
        {
            // Nothing sensible to edit into for a piped tab - its "location" is a set of dropped
            // files, not a path - so the row stays a label there.
            if (_active.PipeFiles != null) return;

            // Empty on This PC rather than its sentinel: there is no path to edit there, and an
            // internal token in the address box would just be noise to type over.
            string here = string.IsNullOrEmpty(_active.CurrentFolder) ? _active.RootPath : _active.CurrentFolder;
            Pane.AddressBox.Text = IsThisPc(here) ? string.Empty : here;   // Browse.cs
            Pane.AddressBox.Visibility   = Visibility.Visible;
            Pane.ScopePathLabel.Visibility = Visibility.Collapsed;

            // Visibility is already set, so focus belongs to this edit immediately. A queued
            // focus can outlive a toolbar click or pane switch and leave the old editor open.
            Pane.AddressBox.Focus();
            Pane.AddressBox.SelectAll();
        }

        private void EndEditAddress()
        {
            Pane.AddressBox.Visibility     = Visibility.Collapsed;
            Pane.ScopePathLabel.Visibility = Visibility.Visible;
        }

        // ── Committing ───────────────────────────────────────────
        internal void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                EndEditAddress();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter) return;
            e.Handled = true;

            string typed = (Pane.AddressBox.Text ?? string.Empty).Trim().Trim('"');
            EndEditAddress();
            if (typed.Length == 0) return;

            // Environment variables are expanded, so %TEMP% and %APPDATA% work the way they do
            // in Explorer's address bar - which is most of why anyone types a path at all.
            try { typed = Environment.ExpandEnvironmentVariables(typed); }
            catch { }

            // A typed file means "open this", not "show me where it is": text goes to an editor
            // tab, a program or script to a terminal in its folder, anything else to its
            // association (Services/AddressTarget.cs). Archives stay places to browse.
            if (File.Exists(typed) && !Services.ArchiveProvider.IsReadable(typed))
            {
                OpenTypedFile(Path.GetFullPath(typed));
                return;
            }

            _ = NavigateTo(typed);   // Browse.cs - reports Str_Status_BadPath itself if it is junk
        }

        private void OpenTypedFile(string path)
        {
            switch (Services.AddressTarget.Classify(path))
            {
                case Services.AddressTargetKind.Edit:
                    OpenForEditing(path);   // EditorTabs.cs
                    break;

                case Services.AddressTargetKind.Terminal:
                    OpenTerminalFor(path);
                    break;

                default:
                    ReportShellOpen(_active, Services.ShellOpen.Open(path));   // ResultsMenu.cs
                    break;
            }
        }

        // A shell in the file's folder with the command typed and waiting for Enter. It is staged
        // on the shell's first prompt (its first directory report), because input sent before
        // the shell is listening is eaten by its startup.
        private void OpenTerminalFor(string path)
        {
            string folder = Path.GetDirectoryName(path)!;
            var profile = Terminal.TerminalProfileStore.ForFolder(folder);
            OpenShell(profile, folder);   // TerminalTabs.cs

            string? command = Services.AddressTarget.StagedCommand(profile.Kind, Path.GetFileName(path));
            var term = Pane.Active?.Term;
            if (command == null || term == null) return;

            // Sent on the first prompt report, the first title, or after the timeout, so cmd and a
            // shell without the prompt script still get the command. A closed terminal drops it.
            var staged = new Terminal.StagedInput(term.Buffer, command,
                text => Dispatcher.BeginInvoke(new Action(() => term.Send(text))), TimeSpan.FromSeconds(3));
            term.Exited += _ => staged.Dispose();
            term.Unloaded += (_, _) => staged.Dispose();
        }

        // Clicking away is a cancel, not a commit: an accidental click elsewhere should never
        // navigate somewhere half-typed.
        internal void AddressBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var owner = ReferenceEquals(sender, RightPane.AddressBox) ? RightPane : LeftPane;
            owner.AddressBox.Visibility = Visibility.Collapsed;
            owner.ScopePathLabel.Visibility = Visibility.Visible;
        }
    }

    internal static class ResultLookup
    {
        /// <summary>First result whose path matches, case-insensitively; null if none.</summary>
        internal static Models.SearchResult? FirstOrDefaultPath(
            this System.Collections.Generic.IEnumerable<Models.SearchResult> items, string path)
        {
            foreach (var r in items)
                if (string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }
    }
}
