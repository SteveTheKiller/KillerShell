using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Controls;
using KillerShell.Services;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class PerformanceTileMotifTests
{
    [Fact]
    public Task EveryMonitorTileCarriesThePinnedPaneInk()
        => RuntimeComponentProcess.Run(nameof(PerformanceTileMotifScenario));
}

public static class PerformanceTileMotifScenario
{
    public static void Run()
    {
        RuntimeComponentProcess.StartApplication();
        var control = new PerformanceMonitorControl(_ => Task.FromResult(PerformanceHardwareInfo.Empty));
        var hardware = new PerformanceHardwareInfo("CPU", "RAM", "GPU", "Network", 8, 1, 1, 1000, [], ["Adapter"], []);
        typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(control, new object[] { hardware });

        // The tiles and their graph wells: every border painting the monitor's cell brush.
        var cells = Descendants(control).OfType<Border>()
            .Where(b => ThemeMotif.GetBaseBrush(b) == "MonitorCellBrush").ToArray();
        // A bare summary-strip graph is see-through and carries no ink of its own; every other one is inked.
        var bare = cells.Where(b => ReferenceEquals(b.Background, Brushes.Transparent)).ToArray();
        var inked = cells.Except(bare).ToArray();
        Assert.NotEmpty(bare);
        Assert.NotEmpty(inked);
        Assert.All(inked, cell => Assert.Equal(MotifPart.Window, ThemeMotif.GetPart(cell)));
        Assert.All(bare, cell => Assert.Equal(MotifPart.None, ThemeMotif.GetPart(cell)));
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Descendants(child)) yield return d;
    }
}
