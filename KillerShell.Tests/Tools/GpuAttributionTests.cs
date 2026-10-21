using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using KillerShell.Services;
using KillerShell.Tests.Terminal;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class GpuAttributionTests
{
    [Fact]
    public Task ReversedCounterOrderDoesNotAssignReadingsToNamedAdapters()
        => RuntimeComponentProcess.Run(nameof(GpuAttributionScenario));
}

public static class GpuAttributionScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var control = new PerformanceMonitorControl(_ => Task.FromResult(PerformanceHardwareInfo.Empty));
        try
        {
            var hardware = new PerformanceHardwareInfo("CPU", "RAM", "GPU", "Network", 8, 1, 1, 1000,
                [], ["Adapter B", "Adapter A"], []);
            typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, new object[] { hardware });
            var values = new Dictionary<string, double> { ["0x0_0xA"] = 90, ["0x0_0xB"] = 10 };
            control.PublishGpuSamples(values.Keys, values, values, values);

            var named = ((IEnumerable)Field(control, "_gpuTiles")).Cast<object>().ToArray();
            Assert.Equal(new[] { "Adapter B", "Adapter A" }, named.Select(t => (string)Field(t, "Description")));
            Assert.All(named, tile => Assert.Equal("-", ((TextBlock)Field(tile, "TileSummaryText")).Text));

            var live = (IDictionary)Field(control, "_gpuCounterTiles");
            object firstA = live["0x0_0xA"]!, firstB = live["0x0_0xB"]!;
            Assert.Equal(90d, Field(firstA, "Primary"));
            Assert.Equal(10d, Field(firstB, "Primary"));
            Assert.Equal("LUID 0x0_0xA", Field(firstA, "Description"));
            Assert.Equal("LUID 0x0_0xB", Field(firstB, "Description"));
            object cpu = Field(control, "_cpuTile");

            var reversed = new Dictionary<string, double> { ["0x0_0xB"] = 25, ["0x0_0xA"] = 75 };
            control.PublishGpuSamples(reversed.Keys, reversed, reversed, reversed);
            Assert.Same(firstA, live["0x0_0xA"]);
            Assert.Same(firstB, live["0x0_0xB"]);
            Assert.Equal(75d, Field(firstA, "Primary"));
            Assert.Equal(25d, Field(firstB, "Primary"));

            var remaining = new Dictionary<string, double> { ["0x0_0xB"] = 40 };
            control.PublishGpuSamples(remaining.Keys, remaining, remaining, remaining);
            Assert.False(live.Contains("0x0_0xA"));
            Assert.Same(firstB, live["0x0_0xB"]);
            Assert.Equal(40d, Field(firstB, "Primary"));
            control.PublishGpuSamples(values.Keys, values, values, values);
            Assert.NotSame(firstA, live["0x0_0xA"]);
            Assert.Same(firstB, live["0x0_0xB"]);
            Assert.Same(cpu, Field(control, "_cpuTile"));
            Assert.All(named, tile => Assert.Equal("-", ((TextBlock)Field(tile, "TileSummaryText")).Text));

            var empty = new Dictionary<string, double>();
            control.PublishGpuSamples(empty.Keys, empty, empty, empty);
            Assert.Empty(live);
            typeof(PerformanceMonitorControl).GetMethod("UpdateSummary", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, null);
            Assert.Equal("-", ((TextBlock)Field(Field(control, "_sumGpu"), "Value")).Text);
        }
        finally { control.Shutdown(); app.Shutdown(); }
    }

    private static object Field(object value, string name)
        => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
}
