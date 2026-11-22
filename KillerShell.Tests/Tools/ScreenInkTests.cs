using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using KillerShell.Controls;
using KillerShell.Services;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// The working screens show the Trash Polka ink at the screen strength, fainter than a pane: the terminal, the text
/// editor, the monitor tiles, and the admin tool tabs (Processes and Services, Events, Registry, Storage), whose roots
/// are inked under their translucent rows.
/// </summary>
public sealed class ScreenInkTests
{
    [Fact]
    public void ScreenStrengthSitsUnderThePaneStrength()
    {
        Assert.True(TrashPolkaInk.ScreenOpacity < TrashPolkaInk.PaneOpacity);
        Assert.True(TrashPolkaInk.ScreenOpacity > 0);
        Assert.True(TrashPolkaInk.CellOpacity < TrashPolkaInk.ScreenOpacity);
        Assert.True(TrashPolkaInk.CellOpacity > 0);
    }

    [Fact]
    public void TerminalAndEditorInkAreScreens()
    {
        XNamespace kc = "clr-namespace:KillerShell.Controls";
        var xaml = XDocument.Load(Path.Combine(RepoRoot(), "Controls", "FilePane.xaml"));
        foreach (string name in new[] { "TerminalInk", "EditorInk" })
        {
            var el = xaml.Descendants().Single(e => (string?)e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == name);
            Assert.Equal("True", (string?)el.Attribute(kc + "ThemeMotif.Screen"));
        }
    }

    [Fact]
    public Task AdminToolsAndMonitorTilesAreInkedScreens() => RuntimeComponentProcess.Run(nameof(ScreenInkScenario));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Controls", "FilePane.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerShell repo root");
    }
}

public static class ScreenInkScenario
{
    public static void Run()
    {
        RuntimeComponentProcess.StartApplication();
        static void Inked(FrameworkElement root, string baseBrush)
        {
            Assert.Equal(MotifPart.Window, ThemeMotif.GetPart(root));
            Assert.Equal(baseBrush, ThemeMotif.GetBaseBrush(root));
            Assert.True(ThemeMotif.GetScreen(root));
        }

        var processes = new ProcessListControl();
        try { Inked(processes, "PaneBrush"); }
        finally { processes.Shutdown(); }
        Inked(new EventViewerControl(), "PaneBrush");
        Inked(new RegistryEditorControl(), "PaneBrush");

        string folder = Path.Combine(Path.GetTempPath(), "ks-screen-ink-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var storage = new StorageAnalyzerControl(folder);
        try
        {
            Inked(storage, "PaneBrush");
            // The map's empty ground is inked too; the file blocks draw over it in their own colors.
            var well = Descendants(storage).OfType<Border>().Single(b => ThemeMotif.GetBaseBrush(b) == "MonitorCellBrush");
            Inked(well, "MonitorCellBrush");
        }
        finally { storage.Shutdown(); Directory.Delete(folder, true); }

        var monitor = new PerformanceMonitorControl(_ => Task.FromResult(PerformanceHardwareInfo.Empty));
        var hardware = new PerformanceHardwareInfo("CPU", "RAM", "GPU", "Network", 8, 1, 1, 1000, [], ["Adapter"], []);
        typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(monitor, new object[] { hardware });
        var tiles = Descendants(monitor).OfType<Border>()
            .Where(b => ThemeMotif.GetBaseBrush(b) == "MonitorCellBrush" && ThemeMotif.GetPart(b) == MotifPart.Window).ToArray();
        Assert.NotEmpty(tiles);
        // The monitor tiles are dashboard cells: darker faces on the lighter page, at the faint tile strength.
        Assert.All(tiles, tile => Assert.True(ThemeMotif.GetCell(tile)));
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Descendants(child)) yield return d;
    }
}
