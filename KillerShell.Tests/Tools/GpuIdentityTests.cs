using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class GpuIdentityTests
{
    [Fact]
    public Task HardwareIdentityKeepsCountersOnTheirNamedAdapter()
        => RuntimeComponentProcess.Run(nameof(GpuIdentityScenario));
}

public static class GpuIdentityScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var hardware = new PerformanceHardwareInfo("CPU", "RAM", "GPU", "Network", 8, 1, 1, 1000,
            [], ["Adapter B", "Adapter A"], [], new Dictionary<string, string>
            {
                ["0x00000000_0x0000000b"] = "Adapter B",
                ["0x00000000_0x0000000a"] = "Adapter A"
            });
        var control = new PerformanceMonitorControl(_ => Task.FromResult(hardware));
        using var source = new HwndSource(new HwndSourceParameters("KillerShell GPU identity fixture")
        { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 600 });
        try
        {
            source.RootVisual = control;
            Pump();
            control.Shutdown();
            var values = new Dictionary<string, double>
            { ["0x00000000_0x0000000a"] = 90, ["0x00000000_0x0000000b"] = 10 };
            control.PublishGpuSamples(values.Keys, values, values, values);
            AssertCard(control, "Adapter A", "90 %");
            AssertCard(control, "Adapter B", "10 %");
            Assert.DoesNotContain(Descendants<TextBlock>(control), t => t.Text.StartsWith("LUID "));

            values = new Dictionary<string, double>
            { ["0x00000000_0x0000000b"] = 75, ["0x00000000_0x0000000a"] = 25 };
            control.PublishGpuSamples(values.Keys, values, values, values);
            AssertCard(control, "Adapter A", "25 %");
            AssertCard(control, "Adapter B", "75 %");

            var empty = new Dictionary<string, double>();
            control.PublishGpuSamples(empty.Keys, empty, empty, empty);
            AssertCard(control, "Adapter A", "-");
            AssertCard(control, "Adapter B", "-");
        }
        finally { control.Shutdown(); source.RootVisual = null; app.Shutdown(); }
    }

    private static void AssertCard(DependencyObject control, string name, string value)
    {
        var description = Assert.Single(Descendants<TextBlock>(control), t => t.Text == name);
        Assert.Contains(Descendants<TextBlock>((DependencyObject)description.Parent), t => t.Text == value);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
