using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Tests.Terminal;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class PerformanceLifetimeTests
{
    [Fact]
    public Task PendingDiscoveryCannotRestartAnUnloadedOrClosedControl()
        => RuntimeComponentProcess.Run(nameof(PerformanceLifetimeScenario));
}

public static class PerformanceLifetimeScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        try { Check(false); Check(true); ReloadBeforeDiscoveryCompletes(); }
        finally { app.Shutdown(); }
    }

    private static void Check(bool shutdown)
    {
        var discovery = new TaskCompletionSource<PerformanceHardwareInfo>();
        int calls = 0;
        var control = new PerformanceMonitorControl(_ => { calls++; return discovery.Task; });
        var panel = new Grid();
        using var source = new HwndSource(new HwndSourceParameters("KillerShell performance lifetime fixture")
        { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 600 });
        source.RootVisual = panel;
        panel.Children.Add(control);
        try
        {
            Pump();
            Assert.True(control.IsLoaded);
            Assert.Equal(1, calls);
            Assert.False(control.IsSampling);
            if (!shutdown)
            {
                panel.Children.Remove(control);
                Pump();
                Assert.False(control.IsLoaded);
            }
            discovery.SetResult(PerformanceHardwareInfo.Empty);
            if (shutdown) control.Shutdown();
            Pump();
            Assert.False(control.IsSampling, "A pending discovery continuation restarted sampling after unload or shutdown.");
            if (!shutdown)
            {
                panel.Children.Add(control);
                Pump();
                Assert.True(control.IsSampling);
                Assert.Equal(1, calls);
                panel.Children.Remove(control);
                Pump();
                Assert.False(control.IsSampling);
                panel.Children.Add(control);
                Pump();
                Assert.True(control.IsSampling);
                Assert.Equal(1, calls);
            }
        }
        finally { control.Shutdown(); source.RootVisual = null; }
    }

    private static void ReloadBeforeDiscoveryCompletes()
    {
        var discovery = new TaskCompletionSource<PerformanceHardwareInfo>();
        int calls = 0;
        var control = new PerformanceMonitorControl(_ => { calls++; return discovery.Task; });
        var panel = new Grid();
        using var source = new HwndSource(new HwndSourceParameters("KillerShell pending reload fixture")
        { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 600 });
        source.RootVisual = panel;
        try
        {
            panel.Children.Add(control); Pump();
            panel.Children.Remove(control); Pump();
            panel.Children.Add(control); Pump();
            Assert.Equal(1, calls);
            Assert.False(control.IsSampling);
            discovery.SetResult(PerformanceHardwareInfo.Empty); Pump();
            Assert.True(control.IsSampling);
            Assert.Equal(1, calls);
        }
        finally { control.Shutdown(); source.RootVisual = null; }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
