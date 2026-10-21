using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace KillerShell.Tests.Shell;

public sealed class SearchCompletionTests
{
    [Fact]
    public Task UnreadableContentShowsPartialResultsInTheLoadedWindow()
        => EditorScenarioProcess.Run(typeof(SearchCompletionScenario).FullName!);
}

public static class SearchCompletionScenario
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-search-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "locked.txt");
        File.WriteAllText(path, "needle");
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
        try
        {
            window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            Console.WriteLine("Loaded search window.");
            var pane = (FrameworkElement)window.FindName("LeftPane");
            var active = ((ItemsControl)pane.FindName("TabStrip")).Items.OfType<SearchTab>().Single(t => t.IsActive);
            var opening = Stopwatch.StartNew();
            while (active.StatusKey != "Str_Status_Listed" && opening.Elapsed < TimeSpan.FromSeconds(10)) Pump();
            Assert.Equal("Str_Status_Listed", active.StatusKey);
            active.PipeFiles = [path];
            active.Groups.Clear();
            var group = new TermGroup();
            group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = "needle" });
            active.Groups.Add(group);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            ((Button)window.FindName("SearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Console.WriteLine("Started locked-file content search.");
            var watch = Stopwatch.StartNew();
            while (active.IsSearching && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Pump();
            }
            Assert.False(active.IsSearching);
            Console.WriteLine("Search completion: " + active.StatusKey + ": " + active.StatusMessage);
            Assert.Equal("Str_Status_SearchPartial", active.StatusKey);
            Assert.Empty(active.Results);
            Assert.Contains("1", active.StatusMessage);
            Assert.Equal("WarnBrush", MainWindow.StatusBrushKey(active.StatusKey, false));
        }
        finally
        {
            Console.WriteLine("Closing search fixture.");
            window?.Close(); app.Shutdown(); Directory.Delete(root, true);
            Console.WriteLine("Search fixture cleaned.");
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
