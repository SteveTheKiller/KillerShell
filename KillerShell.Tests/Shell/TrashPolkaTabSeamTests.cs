using System;
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

/// <summary>
/// Trash Polka: a tool tab meets its page with no bar between them, so the active tab paints the same pinned picture
/// as the page and the two read as one surface. Performance turns the surfaces around (the page is the lighter list
/// surface, the tiles the darker one at the faint tile strength); the admin tools keep the pane surface at screen
/// strength.
/// </summary>
[Trait("Category", "Window")]
public sealed class TrashPolkaTabSeamTests
{
    [Fact]
    public Task ToolTabsJoinTheirPagesOnTrashPolka()
        => RuntimeComponentProcess.Run(nameof(TrashPolkaTabSeamScenario));
}

public static class TrashPolkaTabSeamScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Orange);
        ThemeManager.Apply(Theme.TrashPolka);
        window.SetTabPlacement(bottom: false, persist: false);
        window.FocusPane(pane);
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.Tabs.Count == 2);

        window.OpenPerformanceMonitor();
        ShellWindowFixture.PumpUntil(() => pane.Active?.Perf != null);
        var monitor = pane.Active!.Perf!;
        var hardware = new PerformanceHardwareInfo("CPU", "RAM", "GPU", "Network", 8, 1, 1, 1000, [], ["Adapter"], []);
        typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(monitor, new object[] { hardware });
        window.UpdateLayout();

        // The page face is see-through so the inked list surface under it shows; the tiles keep the darker face.
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(monitor.Background).Color);
        Assert.Same(Application.Current.TryFindResource("MenuBackgroundBrush"), monitor.Resources["MonitorCellBrush"]);
        var tiles = Descendants(monitor).OfType<Border>()
            .Where(b => ThemeMotif.GetBaseBrush(b) == "MonitorCellBrush" && ThemeMotif.GetPart(b) == MotifPart.Window).ToArray();
        Assert.NotEmpty(tiles);
        Assert.All(tiles, tile => Assert.True(ThemeMotif.GetCell(tile)));
        AssertSeamless(window, pane, "tp-perf-tab");

        window.OpenEventViewer();
        ShellWindowFixture.PumpUntil(() => pane.Active?.Events != null);
        window.UpdateLayout();
        AssertSeamless(window, pane, "tp-events-tab");
    }

    /// <summary>
    /// Rows just inside the tab's foot and just inside the page below it come from one continuous picture, so their
    /// averages match. A tab painting the darker menu surface over a lighter page sits well apart.
    /// </summary>
    private static void AssertSeamless(Window window, FilePane pane, string evidence)
    {
        // Let the ink land: the raster layers are built in the background and repainted when they arrive.
        var until = DateTime.UtcNow.AddMilliseconds(600);
        while (DateTime.UtcNow < until)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(20);
        }
        window.UpdateLayout();
        var root = PaneRender.Screen(window);
        double scale = PaneRender.DeviceScale(window);
        byte[] pixels = PaneRender.Pixels(window, evidence + "-window", out int width, out int height, out int stride);
        var tabBorder = MenuBarTabBlendScenario.TabBorder(pane, pane.Active!);
        var ink = Visuals(tabBorder).OfType<Border>().Single(b => b.Name == "tabInk");
        var face = PaneRender.Device(PaneRender.BoundsIn(tabBorder, root), scale);
        PaneRender.SaveMagnified(pixels, stride, height,
            new Int32Rect((int)face.Left - 20, (int)face.Top - 6, (int)face.Width + 40, (int)face.Height + 40), 4, evidence);

        int left = (int)face.Left + 8, right = Math.Min(width - 1, (int)face.Right - 8);
        int seam = (int)Math.Round(face.Bottom);
        double Row(int y) => Enumerable.Range(left, right - left).Average(x => PaneRender.Luma(pixels, stride, x, y));
        double tab = (Row(seam - 3) + Row(seam - 2)) / 2;
        double page = (Row(seam + 2) + Row(seam + 3)) / 2;
        Assert.True(Math.Abs(tab - page) < 10,
            $"{evidence}: tab foot luma {tab:0.0} against page {page:0.0}; tab ink {ThemeMotif.GetBaseBrush(ink)} screen={ThemeMotif.GetScreen(ink)} visible={ink.IsVisible} bg={ink.Background}");
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var d in Visuals(VisualTreeHelper.GetChild(root, i))) yield return d;
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Descendants(child)) yield return d;
    }
}
