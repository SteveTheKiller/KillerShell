using System;
using System.IO;
using System.Linq;
using System.Windows;
using KillerShell.Tools;

// Export: CSV for spreadsheets, HTML for the styled report (HtmlExporter.cs builds
// the report itself). Partial of MainWindow. Column headers stay English
// (machine-readable) per project convention.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // The toolbar's export icon: one button, the format chosen from a flyout, so the
        // location row is not carrying two text buttons. Same Popup + Anim.FadeIn pattern as
        // the theme flyout (ThemeFlyout.cs), so every flyout in the app opens the same way.
        internal void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var p = Pane.ExportPopup;
            p.IsOpen = !p.IsOpen;
            if (p.IsOpen && p.Child is UIElement child) Anim.FadeIn(child);
        }

        internal void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            Pane.ExportPopup.IsOpen = false;   // before the empty-results early return
            var tab = _active;
            if (tab.Results.Count == 0)
            {
                SetTabStatusKey(tab, "Str_Status_NothingExport");
                return;
            }

            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter   = "CSV File|*.csv",
                FileName = $"KillerShell-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                Title    = "Save results as CSV"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Name,Folder,SizeBytes,Modified,Found");
                foreach (var r in tab.Results)
                {
                    string found = string.Join("; ", r.Matches.Select(m =>
                        m.Lines.Count > 0 ? $"{m.Term.ModeName} ({m.Lines.Count})" : m.Term.ModeName));
                    sb.AppendLine(string.Join(",",
                        Csv(r.FileName), Csv(r.Directory), r.SizeBytes.ToString(),
                        Csv(r.ModifiedLabel), Csv(found)));
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                SetTabStatusKey(tab, "Str_Status_Exported", dlg.FileName);
            }
            catch (Exception ex)
            {
                SetTabStatusKey(tab, "Str_Status_ExportFailed", ex.Message);
            }
        }

        private static string Csv(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

        internal void Export_Click(object sender, RoutedEventArgs e)
        {
            Pane.ExportPopup.IsOpen = false;   // before the empty-results early return
            var tab = _active;
            if (tab.Results.Count == 0)
            {
                SetTabStatusKey(tab, "Str_Status_NothingExport");
                return;
            }

            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter   = "HTML Files|*.html",
                FileName = $"KillerShell-{DateTime.Now:yyyyMMdd-HHmmss}.html",
                Title    = "Save results as HTML"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    // Browsing tabs take their path from CurrentFolder, not the search panel's
                    // root box - that box is empty while browsing, so the report used to head
                    // itself "Searched  for everything."
                    new Services.HtmlExporter().Export(dlg.FileName, tab.Results,
                        [.. tab.Groups.SelectMany(g => g.Terms)],
                        tab.IsBrowsing ? tab.CurrentFolder ?? string.Empty : Pane.RootPathBox.Text,
                        tab.IsBrowsing);
                    SetTabStatusKey(tab, "Str_Status_Exported", dlg.FileName);
                    System.Diagnostics.Process.Start(dlg.FileName);
                }
                catch (Exception ex)
                {
                    SetTabStatusKey(tab, "Str_Status_ExportFailed", ex.Message);
                }
            }
        }

        private void ExportStorageAnalyzer(Models.SearchTab tab, StorageAnalyzerControl storage)
        {
            var report = storage.CreateReport();
            if (report == null)
            {
                SetTabStatusKey(tab, "Str_Status_NothingExport");
                return;
            }

            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = "HTML Files|*.html",
                FileName = $"KillerShell-Storage-{DateTime.Now:yyyyMMdd-HHmmss}.html",
                Title = "Save storage report as HTML"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                new Services.StorageHtmlExporter().Export(dlg.FileName, report);
                SetTabStatusKey(tab, "Str_Status_Exported", dlg.FileName);
                System.Diagnostics.Process.Start(dlg.FileName);
            }
            catch (Exception ex)
            {
                SetTabStatusKey(tab, "Str_Status_ExportFailed", ex.Message);
            }
        }
    }
}
