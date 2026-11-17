using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Tools;

// Every Performance graph has its own color, so a trace can be named by color alone: CPU, memory,
// disk read, disk write, network send, network receive and GPU never share one. Checked against
// the real theme files on disk, because a color is only readable against the tile it sits on.
public sealed class PerformanceGraphBrushesTests
{
    [Fact]
    public void EveryThemeGivesEachGraphItsOwnReadableColor()
    {
        string themes = Path.Combine(RepoRoot(), "Themes");
        var files = Directory.GetFiles(themes, "*.xaml");
        Assert.Equal(14, files.Length);
        foreach (string file in files)
            OnSta(() =>
            {
                var theme = (ResourceDictionary)XamlReader.Parse(File.ReadAllText(file));
                PerformanceGraphBrushes.Complete(theme);
                string name = Path.GetFileNameWithoutExtension(file);
                Color tile = Surface(theme);

                var colors = PerformanceGraphBrushes.Keys.Select(key =>
                {
                    var brush = Assert.IsType<SolidColorBrush>(theme[key]);
                    double contrast = Contrast(brush.Color, tile);
                    Assert.True(contrast >= 3.0, $"{name}: {key} {brush.Color} is {contrast:0.00}:1 on its tile {tile}.");
                    return brush.Color;
                }).ToArray();
                Assert.True(colors.Distinct().Count() == colors.Length, $"{name}: two Performance graphs share a color.");
            });
    }

    [Fact]
    public void AColorTheThemeStatesIsKept()
        => OnSta(() =>
        {
            var stated = new SolidColorBrush(Colors.Olive);
            var theme = new ResourceDictionary { [PerformanceGraphBrushes.DiskWrite] = stated };
            PerformanceGraphBrushes.Complete(theme);
            Assert.Same(stated, theme[PerformanceGraphBrushes.DiskWrite]);
            Assert.All(PerformanceGraphBrushes.Keys, key => Assert.True(theme.Contains(key), key));
        });

    // The tile the graphs are drawn on: the theme's own cell color when it states one (98SE's black
    // readouts), otherwise the list pane surface the Performance tab paints its cards with.
    private static Color Surface(ResourceDictionary theme)
    {
        foreach (string key in new[] { "MonitorCellBrush", "ListPaneBrush", "PaneBrush" })
            if (theme.Contains(key) && theme[key] is SolidColorBrush solid) return solid.Color;
        throw new InvalidOperationException("The theme states no tile surface.");
    }

    private static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerShell.csproj");
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
