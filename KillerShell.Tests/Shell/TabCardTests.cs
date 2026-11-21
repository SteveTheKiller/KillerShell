using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TabCardTests
{
    [Fact]
    public Task ATabsCardNamesItAndMovesItToTheOtherPane()
        => RuntimeComponentProcess.Run(nameof(TabCardScenario));

    [Fact]
    public Task ABlockedFolderProbeDoesNotDelayTheTabCard()
        => RuntimeComponentProcess.Run(nameof(TabCardBlockedProbeScenario));

    [Fact]
    public Task TheOverflowDropdownDoesNotWaitOnReachabilityProbes()
        => RuntimeComponentProcess.Run(nameof(TabOverflowBlockedProbeScenario));
}

public static class TabOverflowBlockedProbeScenario
{
    public static void Run()
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("application:///"));
        var app = RuntimeComponentProcess.StartApplication();
        MainWindow? window = null;
        using var release = new ManualResetEventSlim();
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
            window.TabIconPathExists = (_, _) => { release.Wait(TimeSpan.FromSeconds(10)); return false; };

            pane.Tabs.Clear();
            string unique = Guid.NewGuid().ToString("N");
            for (int i = 0; i < 20; i++)
            {
                var tab = new SearchTab("tab " + i)
                {
                    IsBrowsing = true,
                    CurrentFolder = @"\\unavailable-" + unique + @"\share" + i,
                    IsStripVisible = false,
                };
                pane.Tabs.Add(tab);
            }

            var elapsed = Stopwatch.StartNew();
            var menu = window.BuildTabOverflowMenu(pane);
            elapsed.Stop();

            Assert.Equal(20, menu.Items.OfType<MenuItem>().Count());
            Assert.True(elapsed.ElapsedMilliseconds < 100,
                "The overflow dropdown waited " + elapsed.ElapsedMilliseconds + " ms on reachability probes.");
            Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.IsType<System.Windows.Shapes.Ellipse>(item.Icon));
        }
        finally
        {
            release.Set();
            window?.Close();
            app.Shutdown();
        }
    }
}

public static class TabCardBlockedProbeScenario
{
    public static void Run()
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("application:///"));
        var app = RuntimeComponentProcess.StartApplication();
        MainWindow? window = null;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
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
            // The constructor seeds a real tab. Card creation uses its opening handler and
            // does not need to show a window or activate a popup.
            window = new MainWindow();
            var pane = (FilePane)window.FindName("LeftPane");
            var tab = pane.Active;
            string blocked = @"\\unavailable-" + Guid.NewGuid().ToString("N") + @"\share";
            tab.IsBrowsing = true;
            tab.CurrentFolder = blocked;
            window.TabIconPathExists = (path, isDirectory) =>
            {
                if (path != blocked || !isDirectory)
                    return isDirectory ? Directory.Exists(path) : File.Exists(path);
                started.Set();
                try { release.Wait(TimeSpan.FromSeconds(5)); return false; }
                finally { finished.Set(); }
            };
            var card = new ContextMenu
            {
                Tag = "tabcard",
                PlacementTarget = new Border { DataContext = tab },
            };
            var elapsed = Stopwatch.StartNew();
            window.TabPlacementMenu_Opened(card, new RoutedEventArgs());
            elapsed.Stop();

            Assert.True(elapsed.ElapsedMilliseconds < 1000,
                "The tab card waited " + elapsed.ElapsedMilliseconds + " ms for an unavailable folder.");
            Assert.True(started.Wait(1000), "Card creation did not exercise the filesystem probe.");
            Assert.False(finished.IsSet, "The filesystem probe must remain blocked while the card is ready.");
            var head = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardhead");
            var header = Assert.IsType<DockPanel>(head.Header);
            Assert.IsType<System.Windows.Shapes.Ellipse>(header.Children[0]);
        }
        finally
        {
            release.Set();
            if (started.IsSet) finished.Wait(5000);
            window?.Close();
            app.Shutdown();
        }
    }
}

public static class TabCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        window.FocusPane(left);
        left.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => left.Tabs.Count == 2 && left.Tabs[1].IsBrowsing);
        var tab = left.Tabs[1];

        // One pane: the card names the tab, its tiles keep their places with Other pane dimmed, a
        // folder tab can pipe its rows, and the strip placement tiles follow.
        var card = OpenCard(window, left, tab);
        var head = Tagged(card, "cardhead");
        Assert.Equal(tab.Title, AutomationProperties.GetName(head));
        // The card is customizable, and Pipe starts in its Hidden drawer for two full rows.
        Assert.Equal(new[] { "newtab", "otherpane", "newwindow", "close", "tabstop", "tabsbottom" },
            KillerShell.Controls.CardMenus.Buttons(card).Select(b => b.CommandParameter as string).ToArray());
        Assert.False(KillerShell.Controls.CardMenus.TileButton(card, "otherpane")!.IsEnabled);
        Assert.Equal("Alt+Shift+Down", AutomationProperties.GetAcceleratorKey(KillerShell.Controls.CardMenus.TileButton(card, "tabsbottom")!));

        // Two panes: Other pane appears and moves the tab across.
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active != null && right.Active.IsBrowsing);
        window.FocusPane(left);
        card = OpenCard(window, left, tab);
        var move = KillerShell.Controls.CardMenus.TileButton(card, "otherpane")!;
        Assert.True(move.IsEnabled);
        move.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ShellWindowFixture.PumpUntil(() => right.Tabs.Contains(tab) && !left.Tabs.Contains(tab),
            diagnostic: () => "left=" + left.Tabs.Count + "; right=" + right.Tabs.Count);
    }

    private static ContextMenu OpenCard(MainWindow window, FilePane pane, SearchTab tab)
    {
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => pane.TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement);
        var container = (DependencyObject)pane.TabStrip.ItemContainerGenerator.ContainerFromItem(tab);
        var host = Descendants(container).OfType<FrameworkElement>().First(e => e.ContextMenu is { Tag: "tabcard" });
        var card = host.ContextMenu!;
        card.PlacementTarget = host;
        window.TabPlacementMenu_Opened(card, new RoutedEventArgs());
        return card;
    }

    private static MenuItem Tagged(ContextMenu card, string tag)
        => Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == tag);

    internal static System.Collections.Generic.List<Button> Buttons(MenuItem slot)
    {
        var found = new System.Collections.Generic.List<Button>();
        void Walk(object node)
        {
            if (node is Button button) { found.Add(button); return; }
            if (node is DependencyObject d) foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(slot.Header);
        return found;
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
