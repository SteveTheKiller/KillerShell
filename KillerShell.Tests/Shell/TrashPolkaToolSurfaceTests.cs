using System;
using System.Collections.Generic;
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
/// Trash Polka, measured on the rendered window at 100% with the tabs on top and at 125% with the tabs on the bottom:
/// the Performance page around the tiles is the file browser's lighter surface and the tiles are clearly darker, and
/// the Storage map ground is that same lighter surface. Checking only that a tab matches its page let "page and tiles
/// both dark" ship in 1.6.1.
/// </summary>
[Trait("Category", "Window")]
public sealed class TrashPolkaToolSurfaceTests
{
    [Fact]
    public Task ToolPagesUseTheFileBrowserSurface()
        => RuntimeComponentProcess.Run(nameof(TrashPolkaToolSurfaceScenario));
}

public static class TrashPolkaToolSurfaceScenario
{
    public static void Run()
    {
        // The theme is in place before the window exists, as when the app starts on Trash Polka.
        ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Red);
        ThemeManager.Apply(Theme.TrashPolka);
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        window.SetTabPlacement(bottom: false, persist: false);
        window.FocusPane(pane);
        Settle(window);
        var files = pane.Active!;

        window.OpenPerformanceMonitor();
        ShellWindowFixture.PumpUntil(() => pane.Active?.Perf != null);
        var perf = pane.Active!;
        var hardware = new PerformanceHardwareInfo("CPU", "RAM", "GPU", "Network", 8, 1, 1, 1000, [], ["Adapter"], []);
        typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(perf.Perf, new object[] { hardware });

        window.OpenStorageAnalyzer();
        ShellWindowFixture.PumpUntil(() => pane.Active?.Storage != null);
        var storage = pane.Active!;

        var reports = new List<string>();
        var failures = new List<string>();
        Measure("100-top");
        window.ApplyAppScale(1.25);
        window.SetTabPlacement(bottom: true, persist: false);
        Measure("125-bottom");

        string? evidence = Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
            System.IO.File.WriteAllText(System.IO.Path.Combine(evidence, "tp-surface.txt"), string.Join(Environment.NewLine, reports));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Concat(reports)));

        void Measure(string label)
        {
            var root = PaneRender.Screen(window);

            Show(files);
            byte[] pixels = PaneRender.Pixels(window, "tp-surface-" + label + "-files", out int width, out int height, out int stride);
            double scale = PaneRender.DeviceScale(window);
            var list = PaneRender.Device(PaneRender.BoundsIn(pane.ResultsList, root), scale);
            double fileSurface = Median(Sample(pixels, stride, width, height, list, _ => true));

            Show(perf);
            pixels = PaneRender.Pixels(window, "tp-surface-" + label + "-perf", out width, out height, out stride);
            var host = PaneRender.Device(PaneRender.BoundsIn(perf.Perf!, root), scale);
            var tiles = Descendants(perf.Perf!).OfType<Border>()
                .Where(b => ThemeMotif.GetCell(b) && b.IsVisible && b.ActualWidth > 40 && b.ActualHeight > 40)
                .Select(b => PaneRender.Device(PaneRender.BoundsIn(b, root), scale)).ToArray();
            Assert.NotEmpty(tiles);
            double gutter = Median(Sample(pixels, stride, width, height, host,
                p => !tiles.Any(t => Inflate(t, 3).Contains(p))));
            double tile = Median(Sample(pixels, stride, width, height, host,
                p => tiles.Any(t => Inflate(t, -6).Contains(p))));

            Show(storage);
            pixels = PaneRender.Pixels(window, "tp-surface-" + label + "-storage", out width, out height, out stride);
            var well = Descendants(storage.Storage!).OfType<Border>().First(b => b.Child?.GetType().Name == "TreemapSurface");
            var map = PaneRender.Device(PaneRender.BoundsIn(well, root), scale);
            double ground = Median(Sample(pixels, stride, width, height, Inflate(map, -4), _ => true));

            string report = $"{label}: files {fileSurface:0.0}, perf page {gutter:0.0}, perf tile {tile:0.0}, storage map {ground:0.0}";
            reports.Add(report);
            if (Math.Abs(gutter - fileSurface) >= 8) failures.Add(label + ": Performance page is not the file browser surface");
            if (gutter - tile <= 12) failures.Add(label + ": Performance tiles do not stand darker than the page");
            if (Math.Abs(ground - fileSurface) >= 8) failures.Add(label + ": Storage map is not the file browser surface");
        }

        void Show(KillerShell.Models.SearchTab tab)
        {
            window.GetType().GetMethod("ActivateTab", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, new object[] { tab, true });
            Settle(window);
        }
    }

    private static void Settle(Window window)
    {
        // The ink layers are built in the background and repainted when they land.
        var until = DateTime.UtcNow.AddMilliseconds(900);
        while (DateTime.UtcNow < until)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(20);
        }
        window.UpdateLayout();
    }

    private static Rect Inflate(Rect r, double by)
    {
        if (r.Width + 2 * by <= 0 || r.Height + 2 * by <= 0) return Rect.Empty;
        r.Inflate(by, by);
        return r;
    }

    private static List<double> Sample(byte[] pixels, int stride, int width, int height, Rect area, Func<Point, bool> keep)
    {
        var values = new List<double>();
        int x0 = Math.Max(0, (int)area.Left), x1 = Math.Min(width - 1, (int)area.Right);
        int y0 = Math.Max(0, (int)area.Top), y1 = Math.Min(height - 1, (int)area.Bottom);
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
                if (keep(new Point(x + 0.5, y + 0.5))) values.Add(PaneRender.Luma(pixels, stride, x, y));
        return values;
    }

    private static double Median(List<double> values)
    {
        Assert.NotEmpty(values);
        values.Sort();
        return values[values.Count / 2];
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
