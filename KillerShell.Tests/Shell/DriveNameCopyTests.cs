using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class DriveNameCopyTests
{
    [Fact]
    public Task CopyNameUsesDriveLabelsOrRootsAndNeverCopiesAnEmptySelection()
        => EditorScenarioProcess.Run("KillerShell.Tests.Shell.DriveNameCopyScenario");
}

public static class DriveNameCopyScenario
{
    public static void Run()
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("http://localhost/"));
        var app = RuntimeComponentProcess.StartApplication();
        MainWindow? window = null;
        IDataObject? previous = Clipboard.GetDataObject();
        var saved = new DataObject();
        if (previous != null)
            foreach (string format in previous.GetFormats(false))
            {
                object? value = previous.GetData(format, false);
                if (value != null) saved.SetData(format, value, false);
            }
        try
        {
            ThemeManager.GetSetting = key => key switch
            {
                "HomeFolder" => Path.GetTempPath(),
                "RememberTabs" => "0",
                "CheckForUpdatesOnStartup" => "0",
                "CloseToTray" => "0",
                _ => null,
            };
            ThemeManager.Initialize();
            window = new MainWindow();
            var pane = (FilePane)window.FindName("LeftPane");
            void Select(params SearchResult[] rows)
            {
                pane.ResultsList.SelectedItems.Clear();
                pane.ResultsList.ItemsSource = rows;
                foreach (var row in rows) pane.ResultsList.SelectedItems.Add(row);
            }

            Select(new SearchResult { FilePath = @"X:\", FileName = "Data (X:)", IsDirectory = true });
            window.MenuCopyName_Click(window, new RoutedEventArgs());
            Assert.Equal("Data (X:)", Clipboard.GetText());

            Select(new SearchResult { FilePath = @"X:\", IsDirectory = true });
            window.MenuCopyName_Click(window, new RoutedEventArgs());
            Assert.Equal("X:", Clipboard.GetText());

            Select(new SearchResult { FilePath = @"X:\folder\report.txt", FileName = "report.txt" });
            window.MenuCopyName_Click(window, new RoutedEventArgs());
            Assert.Equal("report.txt", Clipboard.GetText());

            Select(new SearchResult { FilePath = @"X:\first.txt" }, new SearchResult { FilePath = @"X:\second.txt" });
            window.MenuCopyName_Click(window, new RoutedEventArgs());
            Assert.Equal("first.txt" + Environment.NewLine + "second.txt", Clipboard.GetText());
            Clipboard.SetText("keep this clipboard");
            string status = pane.Active.StatusMessage;
            Select(new SearchResult());
            window.MenuCopyName_Click(window, new RoutedEventArgs());
            Assert.Equal("keep this clipboard", Clipboard.GetText());
            Assert.Equal(status, pane.Active.StatusMessage);
        }
        finally
        {
            if (previous != null) Clipboard.SetDataObject(saved, true);
            else Clipboard.Clear();
            window?.Close();
            app.Shutdown();
        }
    }
}
