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

    [Fact]
    public Task EventMenuIsACard()
        => RuntimeComponentProcess.Run(nameof(EventCardScenario));

    [Fact]
    public Task RegistryKeyAndValueMenusAreCards()
        => RuntimeComponentProcess.Run(nameof(RegistryCardScenario));
}

public static class RegistryCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        MainWindow.DemoMode = true;
        var window = fixture.Window;
        var control = new RegistryEditorControl();
        window.Pane.Tabs.Add(new SearchTab("Registry") { Registry = control });
        MenuBarTabBlendScenario.SelectTab(window.Pane, window.Pane.Tabs.Last());
        try
        {
            var node = new RegistryNode(@"HKEY_CURRENT_USER\Software\KillerTools", "KillerTools");
            typeof(RegistryEditorControl).GetMethod("BuildKeyCard", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, [node]);
            var card = ToolCardProbe.OpenCard();
            ToolCardProbe.AssertCard(card, "KillerTools",
                ToolCardProbe.L("Str_Menu_RegNewKey"), ToolCardProbe.L("Str_Menu_RegRename"),
                ToolCardProbe.L("Str_Menu_RegDelete"), ToolCardProbe.L("Str_Menu_RegCopyPath"));
            Assert.Single(card.Items.OfType<MenuItem>(), item => Equals(item.Header, ToolCardProbe.L("Str_Menu_RegNew")));
            Assert.Single(card.Items.OfType<MenuItem>(), item => Equals(item.Header, ToolCardProbe.L("Str_Menu_RegCopyName")));
            card.IsOpen = false;
            ShellWindowFixture.PumpUntil(() => !card.IsOpen);

            var row = new RegistryValueRow("Theme", Microsoft.Win32.RegistryValueKind.String, "Dark");
            typeof(RegistryEditorControl).GetMethod("BuildValueCard", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, [node, row]);
            card = ToolCardProbe.OpenCard();
            ToolCardProbe.AssertCard(card, "Theme",
                ToolCardProbe.L("Str_Menu_RegModify"), ToolCardProbe.L("Str_Menu_RegRename"),
                ToolCardProbe.L("Str_Menu_RegDelete"), ToolCardProbe.L("Str_Menu_RegCopyName"));
            card.IsOpen = false;
        }
        finally { control.Shutdown(); MainWindow.DemoMode = false; }
    }
}

public static class EventCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        MainWindow.DemoMode = true;
        var window = fixture.Window;
        var control = new EventViewerControl();
        window.Pane.Tabs.Add(new SearchTab("Events") { Events = control });
        MenuBarTabBlendScenario.SelectTab(window.Pane, window.Pane.Tabs.Last());
        try
        {
            var grid = MenuBarTabBlendScenario.Descendants<DataGrid>(control).Single();
            ShellWindowFixture.PumpUntil(() => grid.Items.Count > 0);
            grid.SelectedIndex = 0;
            var entry = (EventLogEntryInfo)grid.SelectedItem;
            var args = (ContextMenuEventArgs)typeof(ContextMenuEventArgs).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(object), typeof(bool)], null)!.Invoke([grid, true]);
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            grid.RaiseEvent(args);
            var card = ToolCardProbe.OpenCard();
            ToolCardProbe.AssertCard(card, entry.Source,
                ToolCardProbe.L("Str_Menu_EvtCopyMessage"), ToolCardProbe.L("Str_Menu_EvtCopyDetails"),
                ToolCardProbe.L("Str_Evt_DetailsTitle"));
            card.IsOpen = false;
        }
        finally { control.Shutdown(); MainWindow.DemoMode = false; }
    }
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
