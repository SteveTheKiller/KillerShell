using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Models;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class SearchActionAccessibilityTests
{
    [Fact]
    public Task ClearAndAddFilterExposeLocalizedDescriptionsAndShortcutHelp()
        => RuntimeComponentProcess.Run(nameof(SearchActionAccessibilityScenario));

    [Fact]
    public Task RemoveActionsExposeLocalizedNamesAndRemoveOnlyTheChosenRow()
        => RuntimeComponentProcess.Run(nameof(SearchRemoveAccessibilityScenario));
}

public static class SearchRemoveAccessibilityScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        window.Activate();
        var view = new SearchFlyoutLayoutProbe(window);
        var tab = window.Pane.Active;
        foreach (var locale in new[] { Locale.EnUS, Locale.Fr })
        {
            SearchFooterScreen.SwitchLocale(window, locale);
            view.SetRows(2, 0);
            view.Open();
            var add = view.Named<Button>("SearchAddFilterButton");
            var addInvoke = (IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(add).GetPattern(PatternInterface.Invoke);
            addInvoke.Invoke();
            ShellWindowFixture.PumpUntil(() => tab.Filters.Count == 1);
            addInvoke.Invoke();
            ShellWindowFixture.PumpUntil(() => tab.Filters.Count == 2);
            var group = Assert.Single(tab.Groups);
            var keptTerm = group.Terms[0];
            var removedTerm = group.Terms[1];
            var keptFilter = tab.Filters[0];
            var removedFilter = tab.Filters[1];
            var termButton = Assert.Single(SearchFlyoutLayoutProbe.Descendants<Button>(view.Terms),
                button => ReferenceEquals(button.DataContext, removedTerm) && Equals(button.Content, "\uE8BB"));
            var filterButton = Assert.Single(SearchFlyoutLayoutProbe.Descendants<Button>(view.Filters),
                button => ReferenceEquals(button.DataContext, removedFilter) && Equals(button.Content, "\uE8BB"));
            foreach (var action in new[] { (Button: termButton, Text: locale == Locale.Fr ? "Supprimer le terme de recherche" : "Remove search term"),
                (Button: filterButton, Text: locale == Locale.Fr ? "Supprimer le filtre" : "Remove filter") })
            {
                action.Button.BringIntoView();
                view.Refresh();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                var peer = UIElementAutomationPeer.CreatePeerForElement(action.Button);
                string directory = Environment.GetEnvironmentVariable("KILLERSHELL_TEST_EVIDENCE") ?? Path.GetTempPath();
                File.WriteAllText(Path.Combine(directory, locale + "-" + (ReferenceEquals(action.Button, termButton) ? "term" : "filter") + ".txt"),
                    "Name=" + peer.GetName() + Environment.NewLine + "HelpText=" + peer.GetHelpText()
                    + Environment.NewLine + "ToolTip=" + action.Button.ToolTip);
                Assert.Equal(action.Text, peer.GetName());
                Assert.Equal(action.Text, peer.GetHelpText());
                Assert.Equal(action.Text, Assert.IsType<string>(action.Button.ToolTip));
            }
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(termButton).GetPattern(PatternInterface.Invoke)).Invoke();
            ShellWindowFixture.PumpUntil(() => group.Terms.Count == 1);
            Assert.Same(keptTerm, Assert.Single(group.Terms));
            Assert.Equal(2, tab.Filters.Count);
            Assert.DoesNotContain(SearchFlyoutLayoutProbe.Descendants<Button>(view.Terms), button => ReferenceEquals(button.DataContext, removedTerm));
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(filterButton).GetPattern(PatternInterface.Invoke)).Invoke();
            ShellWindowFixture.PumpUntil(() => tab.Filters.Count == 1);
            Assert.Same(keptFilter, Assert.Single(tab.Filters));
            Assert.Same(keptTerm, Assert.Single(group.Terms));
            Assert.DoesNotContain(SearchFlyoutLayoutProbe.Descendants<Button>(view.Filters), button => ReferenceEquals(button.DataContext, removedFilter));
            view.Close();
        }
    }
}

public static class SearchActionAccessibilityScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        window.Activate();
        var view = new SearchFlyoutLayoutProbe(window);
        foreach (var locale in new[] { Locale.EnUS, Locale.Fr })
        {
            SearchFooterScreen.SwitchLocale(window, locale);
            view.Open();
            var search = view.Named<Button>("SearchButton");
            var clear = Assert.Single(SearchFlyoutLayoutProbe.Descendants<Button>((Grid)search.Parent),
                button => Equals(button.Content, window.FindResource("Str_Btn_Clear")));
            var add = view.Named<Button>("SearchAddFilterButton");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            foreach (var action in new[] { (Button: clear, Key: "Str_Ks_Clear", Chord: "Ctrl+Shift+L"),
                (Button: add, Key: "Str_Ks_AddFilter", Chord: "Shift+F7") })
            {
                action.Button.BringIntoView();
                view.Refresh();
                var peer = UIElementAutomationPeer.CreatePeerForElement(action.Button);
                string description = (string)window.FindResource(action.Key);
                string help = description + " (" + action.Chord + ")";
                string directory = Environment.GetEnvironmentVariable("KILLERSHELL_TEST_EVIDENCE") ?? Path.GetTempPath();
                File.WriteAllText(Path.Combine(directory, locale + "-" + action.Key + ".txt"),
                    "Name=" + peer.GetName() + Environment.NewLine + "HelpText=" + peer.GetHelpText()
                    + Environment.NewLine + "ToolTip=" + action.Button.ToolTip);
                Assert.Equal(description, peer.GetName());
                Assert.Equal(help, peer.GetHelpText());
                Assert.Equal(help, Assert.IsType<string>(action.Button.ToolTip));
            }
            view.Close();
        }
    }
}
