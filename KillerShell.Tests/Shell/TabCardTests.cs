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

        // One pane: the card names the tab, offers New tab, New window and Close (no Other pane),
        // and a folder tab can pipe its rows.
        var card = OpenCard(window, left, tab);
        var head = Tagged(card, "cardhead");
        Assert.Equal(tab.Title, AutomationProperties.GetName(head));
        Assert.Equal(3, Buttons(Tagged(card, "cardquick")).Count(b => b.Visibility == Visibility.Visible));
        Assert.Contains(card.Items.OfType<MenuItem>(), item => item.Tag as string == "pipe");
        Assert.Contains(card.Items.OfType<MenuItem>(), item => item.InputGestureText == "Alt+Shift+Down");

        // Two panes: Other pane appears and moves the tab across.
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active != null && right.Active.IsBrowsing);
        window.FocusPane(left);
        card = OpenCard(window, left, tab);
        var move = Assert.Single(Buttons(Tagged(card, "cardquick")), b => (string?)b.CommandParameter == "otherpane");
        Assert.Equal(Visibility.Visible, move.Visibility);
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
