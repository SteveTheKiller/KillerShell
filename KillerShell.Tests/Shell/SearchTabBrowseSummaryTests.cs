using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class SearchTabBrowseSummaryTests
{
    [Fact]
    public Task BrowsingAwayFromSearchResultsDropsTheMatchSummary()
        => RuntimeComponentProcess.Run(nameof(SearchTabBrowseSummaryScenario));
}

public static class SearchTabBrowseSummaryScenario
{
    // A results tab that opens a folder is a folder listing from then on. The pane's footer must
    // stop saying "14 matches  name: invoice" under a listing that is no longer those matches.
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        MainWindow? window = null;
        try
        {
            ThemeManager.GetSetting = key => key switch
            {
                "HomeFolder" => Path.GetTempPath(), "RememberTabs" => "0",
                "CheckForUpdatesOnStartup" => "0", "CloseToTray" => "0", _ => null,
            };
            ThemeManager.Initialize();
            MainWindow.DemoMode = true;
            window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            var pane = window.Pane;
            var results = pane.Tabs.First(t => t.Title == "~\\Documents");
            typeof(MainWindow).GetMethod("SwitchToTab", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [results]);
            ShellWindowFixture.PumpUntil(() => pane.Active == results && !string.IsNullOrEmpty(pane.StatsText.Text));

            var navigate = typeof(MainWindow).GetMethod("NavigateTo", BindingFlags.NonPublic | BindingFlags.Instance)!;
            navigate.Invoke(window, [@"C:\Users\Demo\Pictures", true, false, false]);
            ShellWindowFixture.PumpUntil(() => results.IsBrowsing && pane.ResultsList.Items.Count > 0,
                diagnostic: () => "folder=" + results.CurrentFolder);

            Assert.Equal(string.Empty, pane.StatsText.Text);
            Assert.Equal(string.Empty, pane.QueryText.Text);
            Assert.Equal(string.Empty, results.StatsLabel);
            Assert.Equal(string.Empty, results.QueryLabel);
        }
        finally
        {
            MainWindow.DemoMode = false;
            window?.Close();
            app.Shutdown();
        }
    }
}
