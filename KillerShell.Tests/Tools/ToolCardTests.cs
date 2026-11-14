using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Shell;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ToolCardTests
{
    [Fact]
    public Task ProcessAndServiceMenusAreCards()
        => RuntimeComponentProcess.Run(nameof(ProcessServiceCardScenario));
}

public static class ToolCardProbe
{
    // The card a builder opened: the kit's CardMenu style, a header slot naming the row, and a
    // quick row whose buttons are named for the given commands in order.
    public static ContextMenu OpenCard()
    {
        ContextMenu? found = null;
        ShellWindowFixture.PumpUntil(() => (found = PresentationSource.CurrentSources.OfType<PresentationSource>()
            .Where(s => s.RootVisual != null)
            .SelectMany(s => new[] { s.RootVisual }.Concat(Descendants(s.RootVisual)))
            .OfType<ContextMenu>().FirstOrDefault(m => m.IsOpen)) != null);
        return found!;
    }

    public static void AssertCard(ContextMenu card, string title, params string[] quickNames)
    {
        Assert.Same(Application.Current.FindResource("CardMenu"), card.Style);
        var head = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardhead");
        Assert.Equal(title, AutomationProperties.GetName(head));
        var quick = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardquick");
        var strip = Assert.IsType<UniformGrid>(((Border)quick.Header).Child);
        Assert.Equal(quickNames, strip.Children.OfType<FrameworkElement>().Select(b => AutomationProperties.GetName(b)).ToArray());
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    public static string L(string key) => (string)Application.Current.FindResource(key);
}

public static class ProcessServiceCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var control = new ProcessListControl();
        window.Pane.Tabs.Add(new SearchTab("Processes") { Procs = control });
        MenuBarTabBlendScenario.SelectTab(window.Pane, window.Pane.Tabs.Last());
        try
        {
            var process = new ProcessInfo(4242) { Name = "notepad.exe", User = "Demo", Path = @"C:\Windows\notepad.exe" };
            typeof(ProcessListControl).GetMethod("BuildProcessContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, [process]);
            var card = ToolCardProbe.OpenCard();
            ToolCardProbe.AssertCard(card, "notepad.exe",
                ToolCardProbe.L("Str_Menu_ProcOpenLocation"), ToolCardProbe.L("Str_Menu_ProcKill"),
                ToolCardProbe.L("Str_Menu_ProcRestart"), ToolCardProbe.L("Str_Menu_ProcRunAsAdmin"));
            Assert.Single(card.Items.OfType<MenuItem>(), item => item.InputGestureText == "Enter"
                && Equals(item.Header, ToolCardProbe.L("Str_Proc_DetailsTitle")));
            card.IsOpen = false;
            ShellWindowFixture.PumpUntil(() => !card.IsOpen);

            var service = new ServiceInfo("Spooler") { DisplayName = "Print Spooler", Status = "Running", Path = @"C:\Windows\System32\spoolsv.exe", CanStop = true };
            typeof(ProcessListControl).GetMethod("BuildServiceContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, [service]);
            card = ToolCardProbe.OpenCard();
            ToolCardProbe.AssertCard(card, "Print Spooler",
                ToolCardProbe.L("Str_Menu_ProcOpenLocation"), ToolCardProbe.L("Str_Menu_SvcStart"),
                ToolCardProbe.L("Str_Menu_SvcStop"), ToolCardProbe.L("Str_Menu_ProcRestart"));
            Assert.Single(card.Items.OfType<MenuItem>(), item => item.InputGestureText == "Enter"
                && Equals(item.Header, ToolCardProbe.L("Str_Svc_DetailsTitle")));
            card.IsOpen = false;
        }
        finally { control.Shutdown(); }
    }
}
