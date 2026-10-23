using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Editing;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Terminal;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class PaneLocalizationTests
{
    [Fact]
    public Task LanguageSwitchRefreshesBothPanesAndHiddenToolModels()
        => RuntimeComponentProcess.Run(nameof(PaneLocalizationScenario));
}

public static class PaneLocalizationScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        var leftTab = AddTab(left, "Left query");
        var rightTab = AddTab(right, "Right query");
        left.Active = leftTab;
        right.Active = rightTab;
        window.FocusPane(left);
        window.FocusPane(right);
        window.FocusPane(left);
        ShellWindowFixture.PumpUntil(() => left.ResultsList.Items.Count == 1 && right.ResultsList.Items.Count == 1);
        left.ResultsList.SelectedItem = leftTab.Results[0];
        right.ResultsList.SelectedItem = rightTab.Results[0];
        leftTab.IsSearching = true;
        var all = new List<SearchTab> { leftTab, rightTab };
        foreach (var pane in new[] { left, right })
        {
            var terminal = AddTab(pane, "Terminal"); terminal.Term = new TerminalControl(TerminalSkin.Default); all.Add(terminal);
            var editor = AddTab(pane, "Editor"); editor.Editor = new EditorControl(""); all.Add(editor);
            var processes = AddTab(pane, "Processes"); processes.Procs = new ProcessListControl(); all.Add(processes);
            var events = AddTab(pane, "Events"); events.Events = new EventViewerControl(); all.Add(events);
            var performance = AddTab(pane, "Performance"); performance.Perf = new PerformanceMonitorControl(); all.Add(performance);
            var registry = AddTab(pane, "Registry"); registry.Registry = new RegistryEditorControl(); all.Add(registry);
            var storage = AddTab(pane, "Storage"); storage.Storage = new StorageAnalyzerControl(null); all.Add(storage);
        }
        var notifications = all.ToDictionary(tab => tab, _ => 0);
        foreach (var tab in all)
            tab.Groups[0].PropertyChanged += (_, e) => { if (e.PropertyName == "ModeLabel") notifications[tab]++; };
        try
        {
            Switch(window, Locale.Fr);
            Verify(all, notifications);
            Assert.Same(left, window.Pane);
            Assert.True(leftTab.IsSearching);
            VerifyPane(left, leftTab);
            VerifyPane(right, rightTab);
            window.FocusPane(right);
            Switch(window, Locale.De);
            Verify(all, notifications);
            Assert.Same(right, window.Pane);
            VerifyPane(left, leftTab);
            VerifyPane(right, rightTab);
            window.ToggleDualPane();
            Assert.Same(left, window.Pane);
            Switch(window, Locale.Es);
            Verify(all, notifications);
            Assert.Same(left, window.Pane);
            window.ToggleDualPane();
            VerifyPane(right, rightTab);
            Assert.True(leftTab.IsSearching);
        }
        finally
        {
            foreach (var tab in all)
            {
                tab.Term?.Close(); tab.Procs?.Shutdown(); tab.Events?.Shutdown(); tab.Perf?.Shutdown();
                tab.Registry?.Shutdown(); tab.Storage?.Shutdown();
            }
        }
    }

    private static SearchTab AddTab(FilePane pane, string title)
    {
        var tab = new SearchTab(title)
        {
            StatusKey = "Str_Status_Ready", StatusMessage = "Ready", ScannedCount = 23,
            ScannedLabel = "23 files scanned", StatsLabel = "1 matches", QueryLabel = "name: needle",
            PipeFiles = new List<string> { "fixture.txt" }, PipeArgs = new object[] { 1, "source", "query" },
            PipeLabel = "1 results from source | query"
        };
        var group = new TermGroup();
        group.Terms.Add(new SearchTerm { Pattern = "needle", MatchCount = 7 });
        tab.Groups.Add(group);
        tab.Results.Add(new SearchResult { FileName = "fixture.txt", FilePath = "fixture.txt" });
        pane.Tabs.Add(tab);
        return tab;
    }

    private static void Switch(MainWindow window, Locale locale)
    {
        ShellWindowFixture.Click(window, "LangButton");
        var menu = (ContextMenu)window.FindName("LangMenu");
        var columns = (StackPanel)menu.Items[0];
        var radio = columns.Children.OfType<StackPanel>().SelectMany(column => column.Children.OfType<RadioButton>())
            .Single(button => Equals(button.Tag, locale.ToString()));
        radio.IsChecked = true;
        menu.IsOpen = false;
    }

    private static void Verify(IEnumerable<SearchTab> tabs, Dictionary<SearchTab, int> notifications)
    {
        foreach (var tab in tabs)
        {
            Assert.Equal(MainWindow.LocStatic("Str_Status_Ready"), tab.StatusMessage);
            Assert.Equal(string.Format(MainWindow.LocStatic("Str_Status_Scanned"), "23"), tab.ScannedLabel);
            Assert.Equal(string.Format(MainWindow.LocStatic("Str_Count_Matches"), "1"), tab.StatsLabel);
            Assert.Equal(MainWindow.LocStatic("Str_Mode_Name") + ": needle", tab.QueryLabel);
            Assert.Equal(string.Format(MainWindow.LocStatic("Str_Pipe_Scope"), tab.PipeArgs!), tab.PipeLabel);
            Assert.True(notifications[tab] > 0, "The model's localized labels did not notify: " + tab.Title);
            Assert.Equal(7, tab.Groups[0].Terms[0].MatchCount);
        }
    }

    private static void VerifyPane(FilePane pane, SearchTab tab)
    {
        Assert.Same(tab, pane.Active);
        Assert.Same(tab.Results[0], pane.ResultsList.SelectedItem);
        Assert.Equal(tab.QueryLabel, pane.QueryText.Text);
        Assert.Equal(tab.StatsLabel, pane.StatsText.Text);
        Assert.Equal(tab.ScannedLabel, pane.ScannedText.Text);
        Assert.Equal(tab.PipeLabel, pane.ScopePathLabel.Text);
        string[] densityKeys = ["Str_St_DensityRoomy", "Str_St_DensityFull", "Str_St_DensityCompact",
                                "Str_St_DensityTight", "Str_St_DensityMin"];
        string densityTip = MainWindow.LocStatic("Str_TT_Density") + ": "
            + MainWindow.LocStatic(densityKeys[pane.ViewState.Density]);
        foreach (var button in new[] { pane.FileDensityBtn, pane.TermDensityBtn, pane.EditorDensityBtn })
            Assert.Equal(densityTip, button.ToolTip);
    }
}
