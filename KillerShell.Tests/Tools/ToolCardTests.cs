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
using KillerShell.Controls;
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

    [Fact]
    public Task StorageMapMenuIsACard()
        => RuntimeComponentProcess.Run(nameof(StorageCardScenario));

    [Fact]
    public Task DetailsLayoutMenuIsATileCardWithTheLayoutInUseLit()
        => RuntimeComponentProcess.Run(nameof(DetailsLayoutCardScenario));

    [Fact]
    public Task PerformanceCardMenuIsACardNamedForItsCard()
        => RuntimeComponentProcess.Run(nameof(PerformanceCardScenario));
}

public static class DetailsLayoutCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        var menu = pane.DetailsPaneBtn.ContextMenu;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        Assert.Same(Application.Current.FindResource("CardMenu"), menu.Style);
        Assert.Equal(new[] { "detailsbottom", "detailsright", "detailsoff" },
            CardMenus.Buttons(menu).Select(b => b.CommandParameter).OfType<string>().ToArray());
        // A second opening rebuilds the card rather than stacking another grid on it.
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Tag as string == "cardtiles");
        CardMenus.TileButton(menu, "detailsoff")!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ShellWindowFixture.PumpUntil(() => !pane.DetailsPaneOpen);
    }
}

public static class PerformanceCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.OpenPerformanceMonitor();
        ShellWindowFixture.PumpUntil(() => window.Pane.Active?.Perf != null);
        var control = window.Pane.Active.Perf!;
        try
        {
            var panels = (System.Collections.IList)typeof(PerformanceMonitorControl).GetField("_panelCards", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
            ShellWindowFixture.PumpUntil(() => panels.Count > 0);
            var first = panels[0]!;
            const BindingFlags member = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            string id = (string)(first.GetType().GetProperty("Id", member)?.GetValue(first) ?? first.GetType().GetField("Id", member)!.GetValue(first))!;
            typeof(PerformanceMonitorControl).GetMethod("ShowCardMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, [first]);
            var card = ToolCardProbe.OpenCard();
            Assert.Same(Application.Current.FindResource("CardMenu"), card.Style);
            var head = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardhead");
            string title = AutomationProperties.GetName(head);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.NotEqual(id, title);
            Assert.Equal(new[] { "fold", "onecolumn", "multicolumn", "resetlayout" },
                CardMenus.Buttons(card).Select(b => b.CommandParameter).OfType<string>().ToArray());
            card.IsOpen = false;
        }
        finally { control.Shutdown(); }
    }
}

public static class StorageCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var control = new StorageAnalyzerControl(fixture.Root);
        window.Pane.Tabs.Add(new SearchTab("Storage") { Storage = control });
        MenuBarTabBlendScenario.SelectTab(window.Pane, window.Pane.Tabs.Last());
        var folder = System.IO.Path.Combine(fixture.Root, "Big");
        System.IO.Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "blob.bin"), new byte[200000]);
        try
        {
            control.ScanFolder(fixture.Root);
            var scanning = typeof(StorageAnalyzerControl).GetField("_scanning", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ShellWindowFixture.PumpUntil(() => !(bool)scanning.GetValue(control)!);
            var root = typeof(StorageAnalyzerControl).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
            var children = (System.Collections.IEnumerable)root.GetType().GetField("Children", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(root)!
                ?? (System.Collections.IEnumerable)root.GetType().GetProperty("Children", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(root)!;
            var big = children.Cast<object>().First();
            typeof(StorageAnalyzerControl).GetMethod("ShowMapMenu", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, [big, false]);
            var card = ToolCardProbe.OpenCard();
            ToolCardProbe.AssertCard(card, "Big", "opentab", "explorer", "copypath", "bytype", "byfolder", "rescan");
            ToolCardProbe.AssertHeaderDelete(card);
            card.IsOpen = false;
        }
        finally { control.Shutdown(); }
    }
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
            ToolCardProbe.AssertCard(card, "KillerTools", "newkey", "newvalue", "rename", "copypath", "copyname");
            ToolCardProbe.AssertHeaderDelete(card);
            card.IsOpen = false;
            ShellWindowFixture.PumpUntil(() => !card.IsOpen);

            var row = new RegistryValueRow("Theme", Microsoft.Win32.RegistryValueKind.String, "Dark");
            typeof(RegistryEditorControl).GetMethod("BuildValueCard", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, [node, row]);
            card = ToolCardProbe.OpenCard();
            ToolCardProbe.AssertCard(card, "Theme", "modify", "rename", "copyname");
            ToolCardProbe.AssertHeaderDelete(card);
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
            ToolCardProbe.AssertCard(card, entry.Source, "copymessage", "copydetails", "details");
            card.IsOpen = false;
        }
        finally { control.Shutdown(); MainWindow.DemoMode = false; }
    }
}

public static class ToolCardProbe
{
    // The card a builder opened: the kit's CardMenu style, a header slot naming the row, and a
    // tile grid whose tiles carry the given command names in order.
    public static ContextMenu OpenCard()
    {
        ContextMenu? found = null;
        ShellWindowFixture.PumpUntil(() => (found = PresentationSource.CurrentSources.OfType<PresentationSource>()
            .Where(s => s.RootVisual != null)
            .SelectMany(s => new[] { s.RootVisual }.Concat(Descendants(s.RootVisual)))
            .OfType<ContextMenu>().FirstOrDefault(m => m.IsOpen)) != null);
        return found!;
    }

    public static void AssertCard(ContextMenu card, string title, params string[] tileIds)
    {
        Assert.Same(Application.Current.FindResource("CardMenu"), card.Style);
        var head = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardhead");
        Assert.Equal(title, AutomationProperties.GetName(head));
        Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardtiles");
        Assert.Equal(tileIds, CardMenus.Buttons(card).Select(b => b.CommandParameter).OfType<string>().ToArray());
    }

    // Delete sits at the header's right end, named for the command, never as a tile.
    public static void AssertHeaderDelete(ContextMenu card) =>
        Assert.Single(CardMenus.Buttons(card), b => Equals(b.Tag, CardMenus.HeaderDeleteTag));

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
            ToolCardProbe.AssertCard(card, "notepad.exe", "location", "restart", "asadmin", "details", "kill");
            card.IsOpen = false;
            ShellWindowFixture.PumpUntil(() => !card.IsOpen);

            var service = new ServiceInfo("Spooler") { DisplayName = "Print Spooler", Status = "Running", Path = @"C:\Windows\System32\spoolsv.exe", CanStop = true };
            typeof(ProcessListControl).GetMethod("BuildServiceContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, [service]);
            card = ToolCardProbe.OpenCard();
            ToolCardProbe.AssertCard(card, "Print Spooler", "location", "details", "start", "restart", "stop");
            card.IsOpen = false;
        }
        finally { control.Shutdown(); }
    }
}
