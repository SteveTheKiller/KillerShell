using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TabCloseLifecycleTests
{
    [Fact]
    public Task HiddenImmediateAndWindowCloseCompleteOwnedToolCleanup()
        => RuntimeComponentProcess.Run(nameof(TabCloseLifecycleScenario));
}

public static class TabCloseLifecycleScenario
{
    public static void Run()
    {
        var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        var pending = new TaskCompletionSource<PerformanceHardwareInfo>();
        int started = 0, canceled = 0;
        var tool = new PerformanceMonitorControl(token =>
        {
            started++;
            token.Register(() => { canceled++; pending.TrySetCanceled(); });
            return pending.Task;
        });
        var tab = new SearchTab("Closing performance") { Perf = tool };
        try
        {
            ShellWindowFixture.Click(window, "DualPaneRailBtn");
            ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
            ShellWindowFixture.Click(window, "DualPaneRailBtn");
            // No item container exists in a hidden pane: this close must finish synchronously.
            var hidden = new SearchTab("Unrealized tool") { Events = new EventViewerControl() };
            right.Tabs.Add(hidden);
            Assert.Null(right.TabStrip.ItemContainerGenerator.ContainerFromItem(hidden));
            window.CloseTab_Click(new Button { Tag = hidden }, new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain(hidden, right.Tabs);
            Assert.Null(hidden.Events);
            Assert.Same(left, window.Pane);

            ShellWindowFixture.Click(window, "DualPaneRailBtn");
            right.Tabs.Add(tab);
            right.Active = tab;
            window.FocusPane(left);
            window.FocusPane(right);
            window.UpdateLayout();
            ShellWindowFixture.PumpUntil(() => started == 1);
            ShellWindowFixture.PumpUntil(() => right.TabStrip.ItemContainerGenerator.ContainerFromItem(tab) != null);
            Console.WriteLine("client_area_animation=" + SystemParameters.ClientAreaAnimation);
            window.CloseTab_Click(new Button { Tag = tab }, new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(tab, right.Tabs);
            window.FocusPane(left);
            // Dispose closes the actual window while the chip's completion is still pending.
        }
        finally { fixture.Dispose(); }
        Assert.DoesNotContain(tab, right.Tabs);
        Assert.Null(tab.Perf);
        Assert.Equal(1, started);
        Assert.Equal(1, canceled);
        Assert.True(pending.Task.IsCanceled);
        Assert.False(tool.IsSampling);
    }
}
