using KillerShell.Controls;
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
                FileName = $"KillerShell-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.csv",
                Title    = Loc("Str_Export_TitleCsv")
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                Services.CsvExport.Export(dlg.FileName, tab.Results);
                SetTabStatusKey(tab, "Str_Status_Exported", dlg.FileName);
            }
            catch (Exception ex)
            {
                SetTabStatusKey(tab, "Str_Status_ExportFailed", ex.Message);
            }
        }

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
                FileName = $"KillerShell-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.html",
                Title    = Loc("Str_Export_TitleHtml")
            };

            if (dlg.ShowDialog(this) == true)
            {
                // Browsing tabs take their path from CurrentFolder, not the search panel's
                // root box - that box is empty while browsing, so the report used to head
                // itself "Searched  for everything."
                string root = tab.IsBrowsing ? tab.CurrentFolder ?? string.Empty : Pane.RootPathBox.Text;
                var terms = tab.Groups.SelectMany(g => g.Terms).ToList();
                ReportExport(tab, dlg.FileName, ExportAndOpen.Run(
                    () => Services.HtmlExporter.Export(dlg.FileName, tab.Results, [.. terms], root, tab.IsBrowsing),
                    () => System.Diagnostics.Process.Start(dlg.FileName)));
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
                FileName = $"KillerShell-Storage-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.html",
                Title = Loc("Str_Export_TitleStorage")
            };
            if (dlg.ShowDialog(this) != true) return;

            ReportExport(tab, dlg.FileName, ExportAndOpen.Run(
                () => Services.StorageHtmlExporter.Export(dlg.FileName, report),
                () => System.Diagnostics.Process.Start(dlg.FileName)));
        }

        // The file was written even when no browser could open it, so that still reads as exported.
        private void ReportExport(Models.SearchTab tab, string file, ExportAndOpen.Outcome outcome)
        {
            if (outcome.Exported) SetTabStatusKey(tab, "Str_Status_Exported", file);
            else SetTabStatusKey(tab, "Str_Status_ExportFailed", outcome.Error ?? string.Empty);
        }
    }
}
