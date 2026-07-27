using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerShell
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

        private void InitHomeFolder()
        {
            string saved = Services.ThemeManager.GetSetting("HomeFolder") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved)) HomeFolder = saved;
        }

        // ── Entering edit mode ───────────────────────────────────
        internal void ScopeBar_Click(object sender, MouseButtonEventArgs e) => BeginEditAddress();

        internal void BeginEditAddress()
        {
            // Nothing sensible to edit into for a piped tab - its "location" is a set of dropped
            // files, not a path - so the row stays a label there.
            if (_active.PipeFiles != null) return;

            // Empty on This PC rather than its sentinel: there is no path to edit there, and an
            // internal token in the address box would just be noise to type over.
            string here = _active.CurrentFolder ?? _active.RootPath ?? string.Empty;
            Pane.AddressBox.Text = IsThisPc(here) ? string.Empty : here;   // Browse.cs
            Pane.AddressBox.Visibility   = Visibility.Visible;
            Pane.ScopePathLabel.Visibility = Visibility.Collapsed;

            // Focus has to wait for the box to actually be visible, or Focus() lands on a
            // collapsed element and silently does nothing.
            Dispatcher.InvokeAsync(() =>
            {
                Pane.AddressBox.Focus();
                Pane.AddressBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
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

            // A file rather than a folder means "show me this": go to its parent and select it.
            if (File.Exists(typed))
            {
                string? parent = Path.GetDirectoryName(typed);
                if (!string.IsNullOrEmpty(parent)) _ = NavigateToAndSelect(parent!, typed);
                return;
            }

            _ = NavigateTo(typed);   // Browse.cs - reports Str_Status_BadPath itself if it is junk
        }

        // Clicking away is a cancel, not a commit: an accidental click elsewhere should never
        // navigate somewhere half-typed.
        internal void AddressBox_LostFocus(object sender, RoutedEventArgs e) => EndEditAddress();

        private async System.Threading.Tasks.Task NavigateToAndSelect(string folder, string file)
        {
            await NavigateTo(folder);

            var hit = _active.Results.FirstOrDefaultPath(file);
            if (hit == null) return;

            Pane.ResultsList.SelectedItems.Clear();
            Pane.ResultsList.SelectedItems.Add(hit);
            Pane.ResultsList.ScrollIntoView(hit);
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
