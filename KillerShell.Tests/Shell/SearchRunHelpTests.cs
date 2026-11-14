using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class SearchRunHelpTests
{
    [Fact]
    public Task SearchHelpAndAutomationNameFollowStopCompletionAndRestoredTabs()
        => RuntimeComponentProcess.Run(nameof(SearchRunHelpScenario));
}

public static class SearchRunHelpScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        File.WriteAllText(Path.Combine(fixture.Root, "needle.txt"), "needle" + new string('x', 2 * 1024 * 1024));
        var group = new TermGroup();
        group.Terms.Add(new SearchTerm { Pattern = "needle", Mode = SearchTerm.SearchMode.Content });
        pane.Active.Groups.Clear();
        pane.Active.Groups.Add(group);
        pane.Active.IncludePatterns = "*.txt";
        var view = new SearchFlyoutLayoutProbe(window);
        view.Open();
        var button = view.Named<Button>("SearchButton");
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        var observed = new List<(string Help, string Name)>();
        bool cancelFirstRun = true, cancellationInvoked = false;
        var content = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(Button));
        EventHandler changed = (_, _) =>
        {
            if (!Equals(button.Content, MainWindow.LocStatic("Str_Btn_Stop"))) return;
            window.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                if (!Equals(button.Content, MainWindow.LocStatic("Str_Btn_Stop"))) return;
                observed.Add((Help(button), peer.GetName()));
                if (cancelFirstRun)
                {
                    cancellationInvoked = true;
                    Invoke(button);
                }
            }));
        };
        content.AddValueChanged(button, changed);
        try
        {
            Invoke(button);
            ShellWindowFixture.PumpUntil(() => observed.Count == 1 && !pane.Active.IsSearching
                && Equals(button.Content, MainWindow.LocStatic("Str_Btn_Search")));
            Assert.Equal(MainWindow.LocStatic("Str_Btn_Stop") + " (Enter)", observed[0].Help);
            Assert.Equal(MainWindow.LocStatic("Str_Btn_Stop"), observed[0].Name);
            Assert.True(cancellationInvoked, "The actual Stop action must be invoked while search is active.");
            Assert.Equal("Str_Status_Stopped", pane.Active.StatusKey);
            AssertCurrentHelp(button, peer);

            cancelFirstRun = false;
            Invoke(button);
            ShellWindowFixture.PumpUntil(() => observed.Count == 2 && !pane.Active.IsSearching
                && Equals(button.Content, MainWindow.LocStatic("Str_Btn_Search")));
            Assert.Equal(MainWindow.LocStatic("Str_Btn_Stop") + " (Enter)", observed[1].Help);
            Assert.Equal(MainWindow.LocStatic("Str_Btn_Stop"), observed[1].Name);
            Assert.Equal("Str_Status_Done", pane.Active.StatusKey);
            AssertCurrentHelp(button, peer);

            var original = pane.Active;
            Invoke((Button)pane.FindName("NewTabBtn"));
            ShellWindowFixture.PumpUntil(() => pane.Tabs.Count == 2 && !ReferenceEquals(pane.Active, original));
            AssertCurrentHelp(button, peer);
            PaneKeyboardScenario.Raise(window, Key.D1, ModifierKeys.Control);
            ShellWindowFixture.PumpUntil(() => ReferenceEquals(pane.Active, original));
            AssertCurrentHelp(button, peer);
            SearchFooterScreen.SwitchLocale(window, Locale.Fr);
            Assert.Equal(MainWindow.LocStatic("Str_Btn_Search"), button.Content);
            AssertCurrentHelp(button, peer);
        }
        finally { content.RemoveValueChanged(button, changed); }
    }

    private static void AssertCurrentHelp(Button button, AutomationPeer peer)
    {
        string current = button.Content.ToString()!;
        Assert.Equal(current + " (Enter)", Help(button));
        Assert.Equal(current, peer.GetName());
    }

    private static string Help(Button button)
        => button.ToolTip is TextBlock text ? new TextRange(text.ContentStart, text.ContentEnd).Text.Trim()
            : button.ToolTip?.ToString() ?? string.Empty;

    private static void Invoke(Button button)
        => ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
}
