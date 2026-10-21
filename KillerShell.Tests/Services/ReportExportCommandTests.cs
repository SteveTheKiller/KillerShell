using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ReportExportCommandTests
{
    [Theory]
    [InlineData("csv")]
    [InlineData("html")]
    public Task CanceledOverwriteLeavesTheReportUnchanged(string format)
        => EditorScenarioProcess.Run(typeof(ReportExportCommandScenario).FullName!, format);
}

public static class ReportExportCommandScenario
{
    public static void Run(string format)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-export-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "existing." + format);
        File.WriteAllText(path, "previous report");
        Environment.SetEnvironmentVariable("KS_STATE", Path.Combine(root, "state.json"));
        var app = RuntimeComponentProcess.StartApplication();
        var settings = new Dictionary<string, string>
        {
            ["EscQuit"] = "quit", ["RememberTabs"] = "0", ["HomeFolder"] = root,
            ["CheckForUpdatesOnStartup"] = "0", ["CloseToTray"] = "0"
        };
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        ThemeManager.Initialize();
        MainWindow? window = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        try
        {
            window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            var pane = (FrameworkElement)window.FindName("LeftPane");
            var active = ((ItemsControl)pane.FindName("TabStrip")).Items.OfType<SearchTab>().Single(t => t.IsActive);
            active.Results.Add(new SearchResult { FileName = "existing", FilePath = path, Directory = root });
            int stage = 0;
            timer.Tick += (_, _) =>
            {
                var confirm = app.Windows.OfType<ConfirmDialog>().FirstOrDefault();
                if (confirm != null)
                {
                    stage = 2;
                    ((Button)confirm.FindName("CancelBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return;
                }
                var dialog = app.Windows.OfType<FileDialog>().FirstOrDefault();
                if (dialog == null) return;
                if (stage == 0)
                {
                    if (!dialog.NavigationTask.IsCompleted) return;
                    stage = 1;
                    ((TextBox)dialog.FindName("FileNameBox")).Text = path;
                    dialog.Dispatcher.BeginInvoke((Action)(() =>
                        ((Button)dialog.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                }
                else if (stage == 2) dialog.DialogResult = false;
            };
            timer.Start();
            if (format == "csv") window.ExportCsv_Click(window, new RoutedEventArgs());
            else window.Export_Click(window, new RoutedEventArgs());
            Assert.Equal(2, stage);
            Assert.Equal("previous report", File.ReadAllText(path));
            Assert.NotEqual("Str_Status_Exported", active.StatusKey);
        }
        finally
        {
            timer.Stop();
            window?.Close();
            app.Shutdown();
            Directory.Delete(root, true);
        }
    }
}
