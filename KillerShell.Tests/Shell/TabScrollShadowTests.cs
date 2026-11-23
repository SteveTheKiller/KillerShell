using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Services;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Trait("Category", "Window")]
public sealed class TabScrollShadowTests
{
    [Fact]
    public Task BottomTabsCarryTheListShadowAsOneFade()
        => RuntimeComponentProcess.Run(nameof(TabScrollShadowScenario));

    [Fact]
    public Task PerformancePageKeepsItsBottomScrollShadow()
        => RuntimeComponentProcess.Run(nameof(PerformanceScrollShadowScenario));

    [Fact]
    public Task PerformancePaneStopsAtThePagesMinimumWidth()
        => RuntimeComponentProcess.Run(nameof(PerformanceMinimumWidthScenario));

    [Fact]
    public Task PerformanceSplitPaneStopsAtThePagesMinimumWidth()
        => RuntimeComponentProcess.Run(nameof(PerformanceSplitMinimumWidthScenario));

    [Fact]
    public Task PaneCornersOppositeTheTabsMatchAtEveryAppScale()
        => RuntimeComponentProcess.Run(nameof(PaneCornerScenario));
}

/// <summary>
/// Renders the whole shell window, so every sample lands on the pixel the screen would show:
/// the app scale (AppScale.cs) is a LayoutTransform above the panes, and rendering a pane on
/// its own would put its origin on a whole pixel that the window does not give it.
/// </summary>
internal static class PaneRender
{
    internal static FilePane ScrollingFolder(ShellWindowFixture fixture)
    {
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        for (int i = 0; i < 90; i++)
            File.WriteAllText(Path.Combine(fixture.Root, "row-" + i.ToString("00") + ".txt"), "shadow");
        ShellWindowFixture.PumpUntil(() => pane.Active.Results.Count >= 90);
        window.FocusPane(pane);
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.Tabs.Count == 2 && pane.TabBar.Visibility == Visibility.Visible);
        window.UpdateLayout();
        return pane;
    }

    /// <summary>The element whose pixels stand for the screen: the window's content root. A
    /// Window itself renders blank into a RenderTargetBitmap.</summary>
    internal static FrameworkElement Screen(Window window) => (FrameworkElement)window.Content;

    /// <summary>The monitor's own scale: 1.5 on a 150% display. Pixels renders at it, so every
    /// sample is a real device pixel, and DIP coordinates are multiplied by it to find one.</summary>
    internal static double DeviceScale(Window window) => VisualTreeHelper.GetDpi(window).DpiScaleX;

    /// <summary>A DIP rectangle in the window, as device pixels.</summary>
    internal static Rect Device(Rect dips, double scale) => new(dips.X * scale, dips.Y * scale, dips.Width * scale, dips.Height * scale);

    internal static byte[] Pixels(Window window, string name, out int width, out int height, out int stride)
    {
        var root = Screen(window);
        root.UpdateLayout();
        // LayoutUpdated handlers, the pane's device-pixel snap among them, run from the
        // dispatcher after the layout pass, so let those queued passes run before reading pixels.
        for (int pass = 0; pass < 3; pass++)
            root.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => root.UpdateLayout()));
        root.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
        double scale = DeviceScale(window);
        width = (int)Math.Ceiling(root.ActualWidth * scale);
        height = (int)Math.Ceiling(root.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        Save(bitmap, name);
        return pixels;
    }

    internal static double Luma(byte[] pixels, int stride, int x, int y)
    {
        int i = y * stride + x * 4;
        return 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
    }

    internal static void Save(BitmapSource bitmap, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }

    internal static void SaveMagnified(byte[] pixels, int stride, int height, Int32Rect crop, int magnification, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) return;
        var source = BitmapSource.Create(stride / 4, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        int x = Math.Max(0, crop.X), y = Math.Max(0, crop.Y);
        crop = new Int32Rect(x, y, Math.Min(crop.Width, stride / 4 - x), Math.Min(crop.Height, height - y));
        var cropped = new CroppedBitmap(source, crop);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var context = visual.RenderOpen())
            context.DrawImage(cropped, new Rect(0, 0, crop.Width * magnification, crop.Height * magnification));
        var bitmap = new RenderTargetBitmap(crop.Width * magnification, crop.Height * magnification, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Save(bitmap, name);
    }

    /// <summary>Bounds of element in window pixels, render transforms and the app scale included.</summary>
    internal static Rect BoundsIn(FrameworkElement element, Visual root)
        => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    internal static Border? FindNamedBorder(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border b && b.Name == name) return b;
            if (FindNamedBorder(child, name) is Border found) return found;
        }
        return null;
    }

    internal static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroller(child) is ScrollViewer found) return found;
        }
        return null;
    }
}

public static class TabScrollShadowScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = PaneRender.ScrollingFolder(fixture);
        window.SetTabPlacement(bottom: true, persist: false);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => pane.HasBottomTabPeek);
        // Part way down a row, so the list's foot cuts through a line of icons the way a real
        // folder does more often than not.
        var scroller = PaneRender.FindScroller(pane.ResultsList)!;
        scroller.ScrollToVerticalOffset(70);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => scroller.VerticalOffset > 60);

        // The list's own cue steps aside so the overlay is the only shade, never two stacked.
        Assert.Same(Brushes.Transparent, scroller.Resources["ScrollBottomCueBrush"]);

        var tabFace = MenuBarTabBlendScenario.TabBorder(pane, pane.Active);
        var peek = PaneRender.FindNamedBorder(tabFace, "tabListPeek")!;
        ShellWindowFixture.PumpUntil(() => peek.Visibility == Visibility.Visible && peek.Background is VisualBrush);
        byte[] seen = BottomShadow.AssertOneShade(window, pane, "shadow", out Rect face, out int seam, out int stride);


        // The folder carries on into its tab: the row cut off at the list's foot shows through
        // the active tab's face (tabListPeek) and that layer fades out before the tab's foot.
        var brush = Assert.IsType<VisualBrush>(peek.Background);
        Assert.True(brush.Viewbox.Y > 0, "The peek must look just below the viewport.");
        Assert.Equal(brush.Viewbox.Width, brush.Viewport.Width);
        // Rendered: the white paper icons reach into the upper part of the tab's face.
        double brightest = 0;
        for (int py = seam + 2; py < seam + face.Height * 0.4; py++)
            for (int px = (int)face.Left + 2; px < face.Right - 2; px++)
                brightest = Math.Max(brightest, PaneRender.Luma(seen, stride, px, py));
        Assert.True(brightest > 120, "The cut-off icons must show through the tab's face: brightest " + brightest.ToString("0"));

        // Scrolled to the end there is nothing below, so the shadow and the peek go and the
        // list's cue comes back for the next time there is.
        scroller.ScrollToEnd();
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => !pane.HasBottomTabPeek);
        Assert.Equal(Visibility.Collapsed, pane.TabScrollShadow.Visibility);
        Assert.False(scroller.Resources.Contains("ScrollBottomCueBrush"));
    }
}

/// <summary>
/// The Performance page scrolls its tiles under the same bottom shadow as the file list, with
/// the tabs on top (the scroller's own cue) and below (one shade on through the active tab).
/// </summary>
public static class PerformanceScrollShadowScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        window.FocusPane(pane);
        window.Width = 1100; window.Height = 560;
        window.UpdateLayout();
        window.OpenPerformanceMonitor();
        ShellWindowFixture.PumpUntil(() => pane.Active?.Perf != null);
        var hardware = new PerformanceHardwareInfo("CPU", "RAM", "GPU", "Network", 8, 1, 1, 1000, [], ["Adapter"], []);
        typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(pane.Active!.Perf, new object[] { hardware });
        window.UpdateLayout();
        // The monitor's own panels scroller.
        ShellWindowFixture.PumpUntil(() => PaneRender.FindScroller(pane.PerformanceSlot) is ScrollViewer s && s.ScrollableHeight > 30);
        var scroller = PaneRender.FindScroller(pane.PerformanceSlot)!;

        // Tabs on top: the scroller's own 28px cue darkens the page's foot.
        window.SetTabPlacement(bottom: false, persist: false);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => !pane.HasBottomTabPeek);
        double scale = PaneRender.DeviceScale(window);
        Rect page = PaneRender.Device(PaneRender.BoundsIn(scroller, PaneRender.Screen(window)), scale);
        byte[] shaded = PaneRender.Pixels(window, "perf-shadow-top", out _, out int height, out int stride);
        scroller.Resources["ScrollBottomCueBrush"] = Brushes.Transparent;
        byte[] plain = PaneRender.Pixels(window, "perf-shadow-top-plain", out _, out _, out _);
        scroller.Resources.Remove("ScrollBottomCueBrush");
        int foot = (int)page.Bottom - 2;
        double ratio = BottomShadow.RowLuma(shaded, stride, page, foot) / Math.Max(1, BottomShadow.RowLuma(plain, stride, page, foot));
        Assert.True(ratio < 0.85, "Tabs on top: the Performance page's foot must carry the scroll shadow: " + ratio.ToString("0.00"));

        // Tabs below: one shade from the page's foot on through the active tab.
        window.SetTabPlacement(bottom: true, persist: false);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => pane.HasBottomTabPeek);
        BottomShadow.AssertOneShade(window, pane, "perf-shadow-bottom", out _, out _, out _);

        // The cards scroll right to the page's foot: no strip of bare page is held under them.
        // The panels' 4px inset is the stack's margin, which scrolls, never the scroller's
        // padding, which the template turns into a fixed frame.
        Assert.Equal(new Thickness(0), scroller.Padding);
        var presenter = (FrameworkElement)scroller.Template.FindName("PART_ScrollContentPresenter", scroller);
        Rect viewport = PaneRender.BoundsIn(presenter, PaneRender.Screen(window));
        Rect whole = PaneRender.BoundsIn(scroller, PaneRender.Screen(window));
        Assert.InRange(whole.Bottom - viewport.Bottom, -0.5, 0.5);
    }
}

/// <summary>
/// Steve 2026-11-22: the Performance page never scrolls sideways. It first held a minimum width;
/// since 8699069 it wraps to whatever room the pane gives it instead, so it always fits its slot.
/// </summary>
public static class PerformanceMinimumWidthScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        window.FocusPane(pane);
        window.Width = 1100; window.Height = 600;
        window.UpdateLayout();
        window.OpenPerformanceMonitor();
        ShellWindowFixture.PumpUntil(() => pane.Active?.Perf != null);
        var perf = pane.Active!.Perf!;
        var hardware = new PerformanceHardwareInfo("AMD Ryzen 7 5700X 8-Core Processor", "DDR4-3200", "GPU", "Network", 8, 1, 1, 1000, [], ["Realtek PCIe GbE Family Controller"], []);
        typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(perf, new object[] { hardware });
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => perf.ActualWidth > 0);

        // A wide window: the page is exactly as wide as its slot, and nothing scrolls sideways.
        Assert.InRange(perf.ActualWidth, pane.PerformanceSlot.ActualWidth - 0.5, pane.PerformanceSlot.ActualWidth + 0.5);
        Assert.Null(FindAncestorScroller(perf, pane.PerformanceHost));

        // Very wide: the GPU card keeps memory beside utilization. It only moves memory under
        // utilization when a legend item would not fit beside it.
        window.Width = 1900;
        window.UpdateLayout();
        Settle(window);
        var legend = GpuLegend(window, perf);
        Assert.NotNull(legend);
        Assert.True(Grid.GetRow(legend!) == 0, "At a " + perf.ActualWidth + "px page the GPU graphs must stay side by side.");
        AssertLabelsFit(window, perf);

        // The middle of the old cutoff band (legend items wider than half the card).
        window.Width = 1100;
        window.UpdateLayout();
        Settle(window);
        PaneRender.Pixels(window, "perf-mid-single", out _, out _, out _);
        AssertLabelsFit(window, perf);

        // Single pane, squeezed by a wide folder tree and a narrow window: the page wraps to the
        // room it gets instead of holding a minimum width (8699069), so it still fills exactly its
        // slot, nothing scrolls sideways, and every label still fits.
        var tree = (ColumnDefinition)window.FindName("TreeCol");
        tree.Width = new GridLength(420);
        window.UpdateLayout();
        window.Width = 400;
        window.UpdateLayout();
        Settle(window);
        Assert.InRange(perf.ActualWidth, pane.PerformanceSlot.ActualWidth - 0.5, pane.PerformanceSlot.ActualWidth + 0.5);
        Assert.Null(FindAncestorScroller(perf, pane.PerformanceHost));
        PaneRender.Pixels(window, "perf-min-single", out _, out _, out _);
        AssertLabelsFit(window, perf);

    }

    /// <summary>Dual pane with the page on the right: the page wraps instead of holding a
    /// minimum (8699069), so pane B keeps the ordinary pane minimum and the page fits its slot.</summary>
    public static void RunDual()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Width = 1100; window.Height = 600;
        window.UpdateLayout();
        window.ToggleDualPane();
        var right = (FilePane)window.FindName("RightPane");
        var colA = (ColumnDefinition)window.FindName("PaneColA");
        var colB = (ColumnDefinition)window.FindName("PaneColB");
        ShellWindowFixture.PumpUntil(() => right.ActualWidth > 100 && colB.MinWidth > 0);
        double ordinaryMin = colB.MinWidth;
        window.FocusPane(right);
        window.OpenPerformanceMonitor();
        ShellWindowFixture.PumpUntil(() => right.Active?.Perf != null);
        var perfB = right.Active!.Perf!;
        var hardware = new PerformanceHardwareInfo("AMD Ryzen 7 5700X 8-Core Processor", "DDR4-3200", "GPU", "Network", 8, 1, 1, 1000, [], ["Realtek PCIe GbE Family Controller"], []);
        typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(perfB, new object[] { hardware });
        window.UpdateLayout();
        Settle(window);
        Assert.InRange(colB.MinWidth, ordinaryMin - 0.5, ordinaryMin + 0.5);
        Assert.True(window.MinWidth > colA.MinWidth + colB.MinWidth,
            "The window's minimum must hold both panes at their minimums: " + window.MinWidth);

        // Squeeze: the window at its floor wraps the right page into its slot.
        window.Width = 400;
        window.UpdateLayout();
        Settle(window);
        Assert.InRange(perfB.ActualWidth, right.PerformanceSlot.ActualWidth - 0.5, right.PerformanceSlot.ActualWidth + 0.5);
        Assert.Null(FindAncestorScroller(perfB, right.PerformanceHost));
        PaneRender.Pixels(window, "perf-min-dual", out _, out _, out _);
        AssertLabelsFit(window, perfB);

        // A file listing in pane B again: the minimum stays the ordinary pane minimum.
        window.FocusPane(right);
        right.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => right.PerformanceHost.Visibility != Visibility.Visible && Math.Abs(colB.MinWidth - ordinaryMin) < 0.5,
            diagnostic: () => "B MinWidth " + colB.MinWidth);
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 4; i++)
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => window.UpdateLayout()));
    }

    private static ScrollViewer? FindAncestorScroller(DependencyObject child, DependencyObject stop)
    {
        for (var node = VisualTreeHelper.GetParent(child); node != null && node != stop; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer s) return s;
        return null;
    }

    /// <summary>At the minimum every reading (it starts with a digit) and every label of the
    /// app's own fits. Hardware names, and the GPU card's title beside its big reading, shorten
    /// with an ellipsis as they are built to.</summary>
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descend(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var d in Descend(VisualTreeHelper.GetChild(node, i))) yield return d;
    }

    /// <summary>Checks with the widest readings in place, set and measured in one dispatcher
    /// turn so the live sampler cannot overwrite them first.</summary>
    private static void AssertLabelsFit(Window window, FrameworkElement page)
    {
        var cut = new System.Collections.Generic.List<string>();
        window.Dispatcher.Invoke(new Action(() =>
        {
            foreach (var tb in Descend(page).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0 && char.IsDigit(t.Text[0])))
                tb.Text = tb.Text.EndsWith("/s") ? "999.9 MB/s"
                        : System.Text.RegularExpressions.Regex.IsMatch(tb.Text, @"^\d+ %$") ? "100 %"
                        : tb.Text.Contains(" GB (") ? "99.9/99.9 GB (100%)"
                        : tb.Text;
            // The GPU memory legend's readings at their widest.
            if (GpuLegend(window, page) is { } legend)
                foreach (var item in legend.Children.OfType<StackPanel>())
                    item.Children.OfType<TextBlock>().Last().Text = "999.9 MB";
            // A wider reading can move the GPU card's memory graph under utilization, which
            // takes a second layout pass to land.
            page.UpdateLayout();
            page.UpdateLayout();
            cut = LabelsCut(window, page);
        }));
        Assert.True(cut.Count == 0, "Labels cut off at the page's minimum width: " + string.Join(" | ", cut));
    }

    private static System.Collections.Generic.List<string> LabelsCut(Window window, FrameworkElement page)
    {
        var labels = new System.Collections.Generic.HashSet<string>();
        void Collect(ResourceDictionary d)
        {
            foreach (var key in d.Keys)
                if (key is string k && k.StartsWith("Str_", StringComparison.Ordinal) && d[key] is string v) labels.Add(v);
            foreach (var m in d.MergedDictionaries) Collect(m);
        }
        Collect(Application.Current.Resources);
        labels.Remove((string)window.FindResource("Str_Perf_GpuUnassigned"));
        return PerformanceFit.Clipped(page, skip: _ => false)
            .Where(entry => { string text = entry.Split('"')[1]; return char.IsDigit(text[0]) || labels.Contains(text); })
            .ToList();
    }

    /// <summary>The GPU card's memory legend (null with no GPU card), so a test can tell whether
    /// memory sits beside utilization (legend on row 0) or under it (row 2).</summary>
    private static WrapPanel? GpuLegend(Window window, FrameworkElement page)
    {
        string dedicated = (string)window.FindResource("Str_Perf_DedicatedMemory");
        return Descend(page).OfType<WrapPanel>().FirstOrDefault(w => w.IsVisible
            && w.Children.OfType<StackPanel>().Any(item => item.Children.OfType<TextBlock>().Any(t => t.Text == dedicated)));
    }
}

public static class PerformanceSplitMinimumWidthScenario
{
    public static void Run() => PerformanceMinimumWidthScenario.RunDual();
}

/// <summary>Every visible line of text on the Performance page that does not fit its box.</summary>
internal static class PerformanceFit
{
    internal static System.Collections.Generic.List<string> Clipped(FrameworkElement root, Func<DependencyObject, bool>? skip = null)
    {
        var found = new System.Collections.Generic.List<string>();
        double dip = VisualTreeHelper.GetDpi(root).PixelsPerDip;
        void Walk(DependencyObject node)
        {
            if (node is UIElement ui && !ui.IsVisible) return;
            if (skip != null && skip(node)) return;
            if (node is TextBlock tb && !string.IsNullOrEmpty(tb.Text) && tb.ActualWidth > 0)
            {
                var text = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, tb.FlowDirection,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, dip);
                double room = tb.ActualWidth - tb.Padding.Left - tb.Padding.Right;
                // A box can hold all of its text and still be cut by a parent that is narrower
                // (a grid column or a card edge), so the room is the least any of them leaves.
                for (DependencyObject? a = tb; a != null; a = a == root ? null : VisualTreeHelper.GetParent(a))
                {
                    if (a is not FrameworkElement fe) continue;
                    double left = tb.TransformToAncestor(fe).Transform(new Point(0, 0)).X;
                    var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(fe);
                    if (clip != null) room = Math.Min(room, clip.Bounds.Right - left - tb.Padding.Left);
                    if (fe.ClipToBounds || fe == root) room = Math.Min(room, fe.ActualWidth - left - tb.Padding.Left);
                }
                // Room of zero or less is text parked out of view at every width (two digits of
                // that kind show up even on a 1000px page), not text a narrow page cut off.
                if (tb.TextWrapping == TextWrapping.NoWrap && room > 0 && text.WidthIncludingTrailingWhitespace > room + 3)
                    found.Add("\"" + tb.Text + "\" " + room.ToString("0") + "<" + text.WidthIncludingTrailingWhitespace.ToString("0"));
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return found;
    }
}

internal static class BottomShadow
{
    internal static double RowLuma(byte[] pixels, int stride, Rect span, int y)
    {
        double sum = 0; int n = 0;
        for (int px = (int)span.Left + 4; px < span.Right - 4; px++, n++) sum += PaneRender.Luma(pixels, stride, px, y);
        return sum / Math.Max(1, n);
    }

    /// <summary>
    /// Renders with and without TabScrollShadow and checks it is one shade: clear 28px above the
    /// band, already dark at the list's foot, the same on both sides of the seam, darkest at the
    /// tab's foot. Returns the shaded pixels.
    /// </summary>
    internal static byte[] AssertOneShade(Window window, FilePane pane, string name, out Rect face, out int seam, out int stride)
    {
        ShellWindowFixture.PumpUntil(() => pane.TabScrollShadow.Visibility == Visibility.Visible
            && pane.TabScrollShadow.Clip != null && pane.TabScrollShadow.ActualHeight > 28);
        var tabFace = MenuBarTabBlendScenario.TabBorder(pane, pane.Active);
        double scale = PaneRender.DeviceScale(window);
        var root = PaneRender.Screen(window);
        face = PaneRender.Device(PaneRender.BoundsIn(tabFace, root), scale);
        Rect list = PaneRender.Device(PaneRender.BoundsIn(pane.ResultsPane, root), scale);
        Rect overlay = PaneRender.Device(PaneRender.BoundsIn(pane.TabScrollShadow, root), scale);
        Assert.InRange(overlay.Top, list.Bottom - 29 * scale - 2, list.Bottom - 27 * scale + 2);
        Assert.InRange(overlay.Bottom, face.Bottom - 2, face.Bottom + 2);

        byte[] shaded = PaneRender.Pixels(window, name + "-window", out _, out int height, out stride);
        pane.TabScrollShadow.Visibility = Visibility.Collapsed;
        byte[] plain = PaneRender.Pixels(window, name + "-window-plain", out _, out _, out _);
        pane.TabScrollShadow.Visibility = Visibility.Visible;

        int s = stride;
        Rect f = face;
        double Shade(int py) => RowLuma(shaded, s, f, py) / Math.Max(1, RowLuma(plain, s, f, py));

        int top = (int)Math.Ceiling(overlay.Top);
        seam = (int)Math.Round(list.Bottom);
        int foot = (int)Math.Floor(face.Bottom) - 1;
        PaneRender.SaveMagnified(shaded, stride, height,
            new Int32Rect((int)face.Left - 12, top - 6, (int)face.Width + 24, foot - top + 12), 4, name + "-join");

        double above = Shade(top + 2), atSeamList = Shade(seam - 2), atSeamTab = Shade(seam + 2), atFoot = Shade(foot);
        Assert.True(above > 0.95, name + ": the fade must start clear 28px above the band: " + above.ToString("0.00"));
        Assert.True(atSeamList < 0.9, name + ": the list's foot must be shaded before the tab: " + atSeamList.ToString("0.00"));
        Assert.True(Math.Abs(atSeamList - atSeamTab) < 0.08,
            name + ": a line at the seam: list " + atSeamList.ToString("0.00") + " vs tab " + atSeamTab.ToString("0.00"));
        Assert.True(atFoot < atSeamTab - 0.05,
            name + ": the tab's foot must be darker than its join: " + atFoot.ToString("0.00") + " vs " + atSeamTab.ToString("0.00"));
        return shaded;
    }
}

/// <summary>
/// The two pane corners opposite the tab band are mirror images on every rounded theme, at
/// 1:1 and at the fractional app scales where the pane's side edges would otherwise land
/// between device pixels and one corner smear while the other stays crisp
/// (FilePane.xaml.cs SnapPaneSidesToPixels).
/// </summary>
public static class PaneCornerScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = PaneRender.ScrollingFolder(fixture);
        var failures = new System.Collections.Generic.List<string>();
        // Where a scaled edge lands between pixels depends on the window's width, so several
        // widths a pixel or three apart are tried at each scale. Win98 is left out: its page is
        // lit from the top left, white down the left and black down the right, so its two
        // corners are meant to differ.
        var themes = new[] { KillerShell.Services.Theme.Dark, KillerShell.Services.Theme.Light, KillerShell.Services.Theme.Decay };
        // Film grain is a random texture, so the two corners' pixels differ by design wherever
        // it shows; the frame is compared without it.
        Application.Current.Resources["GrainOpacity"] = 0.0;
        // Every connected monitor in turn, so a scaled display (150% and the like) is checked
        // with real per-monitor DPI and not only the one the hidden window happened to open on.
        // The window is sized to fit inside each monitor's work area.
        // One pass per distinct display scale: two 100% monitors exercise the same pixels, and
        // a pass per monitor ran past the runner's time limit on a three-monitor desk.
        var scalesDone = new System.Collections.Generic.HashSet<double>();
        foreach (var monitor in Monitors.All())
        {
            ShellWindowFixture.PumpUntil(() => Monitors.MoveTo(window, monitor),
                diagnostic: () => "could not place the window on " + monitor);
            window.UpdateLayout();
            double displayScale = PaneRender.DeviceScale(window);
            if (!scalesDone.Add(Math.Round(displayScale, 2))) continue;
            double fit = Math.Floor(monitor.Work.Width / displayScale) - 40;
            double baseWidth = Math.Min(1140, fit - 8);
        foreach (double scale in new[] { 1.0, 1.1, 1.25 })
        foreach (double extra in new[] { 0.0, 3.0, 7.0 })
        foreach (KillerShell.Services.Theme theme in themes)
        {
            double windowWidth = baseWidth + extra;
            window.Width = windowWidth;
            window.ApplyAppScale(scale);
            window.UpdateLayout();
            string label = theme + "-display" + (PaneRender.DeviceScale(window) * 100).ToString("0") + "-x" + scale.ToString("0.00") + "-w" + windowWidth;
            KillerShell.Services.ThemeManager.Apply(theme);
            // Down and back up, the way a user who tries the option leaves it; the chrome must
            // come back exactly as it was.
            window.SetTabPlacement(bottom: true, persist: false);
            window.UpdateLayout();
            // Tabs below: the corners opposite the tabs are the pane's top two.
            string? worstTop = Compare(window, pane, label + "-top", top: true);
            if (worstTop != null) failures.Add(label + " (tabs below, top corners): " + worstTop);
            window.SetTabPlacement(bottom: false, persist: false);
            window.UpdateLayout();
            string? worst = Compare(window, pane, label, top: false);
            if (worst != null) failures.Add(label + " (tabs above, bottom corners): " + worst);
        }
        }
        window.ApplyAppScale(1.0);
        window.Width = 1140;
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string Describe(Transform? t) => t == null ? "none" : t.Value.ToString();

    private static string? Compare(Window window, FilePane pane, string label, bool top)
    {
        byte[] pixels = PaneRender.Pixels(window, "corners-" + label, out int width, out int height, out int stride);
        Rect frame = PaneRender.BoundsIn(pane.ResultsPane, PaneRender.Screen(window));
        string? evidence = Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
            File.AppendAllText(Path.Combine(evidence, "corners.txt"), label + " scale " + PaneRender.DeviceScale(window)
                + " image " + width + "x" + height + " frame " + frame.Left.ToString("0.00") + ".." + frame.Right.ToString("0.00")
                + " window " + window.Left + "," + window.Top + " " + window.ActualWidth + "x" + window.ActualHeight
                + " visible w=" + window.IsVisible + " stack=" + pane.PaneStack.IsVisible + " pane=" + pane.IsVisible
                + " state=" + window.WindowState + " paneTransform=" + Describe(pane.ResultsPane.RenderTransform) + Environment.NewLine);

        // The whole frame has to be inside what was rendered, or the samples below read pixels
        // from somewhere else entirely.
        if (frame.Right * PaneRender.DeviceScale(window) > width + 1 || frame.Left < 0)
            return "the pane (" + frame.Left.ToString("0") + ".." + frame.Right.ToString("0") + " DIPs) lies outside the rendered window ("
                + (width / PaneRender.DeviceScale(window)).ToString("0") + " DIPs wide at " + (PaneRender.DeviceScale(window) * 100).ToString("0")
                + "%), window " + window.ActualWidth.ToString("0") + "x" + window.ActualHeight.ToString("0") + " at " + window.Left.ToString("0") + "," + window.Top.ToString("0");

        // The pane's sides land on whole DEVICE pixels at every scale (SnapPaneSidesToPixels).
        // The frame is in DIPs, so it is multiplied by the monitor's own scale first: on a 150%
        // display a correctly snapped edge at device pixel 38 is 25.33 DIPs.
        var dpi = VisualTreeHelper.GetDpi(window);
        frame = PaneRender.Device(frame, dpi.DpiScaleX);
        double deviceLeft = frame.Left, deviceRight = frame.Right;
        if (Math.Abs(deviceLeft - Math.Round(deviceLeft)) > 0.03 || Math.Abs(deviceRight - Math.Round(deviceRight)) > 0.03)
            return "the pane's sides sit between device pixels: " + deviceLeft.ToString("0.00") + ".." + deviceRight.ToString("0.00")
                + " at " + (dpi.DpiScaleX * 100).ToString("0") + "% display scale"
                + " (pane transform " + Describe(pane.ResultsPane.RenderTransform) + ", band " + Describe(pane.TabBar.RenderTransform)
                + ", stack to window " + Describe(pane.PaneStack.TransformToAncestor(PaneRender.Screen(window)) as Transform) + ")";
        int size = 32;
        int left = (int)Math.Round(frame.Left), right = (int)Math.Round(frame.Right);
        // Rows are counted from the frame's edge inward, whichever edge is opposite the tabs.
        int edge = top ? (int)Math.Round(frame.Top) - 1 : (int)Math.Round(frame.Bottom);
        int step = top ? 1 : -1;
        int cropY = top ? edge + 1 : edge - size;
        PaneRender.SaveMagnified(pixels, stride, height, new Int32Rect(left - 2, cropY, size, size), 10, "corner-left-" + label);
        PaneRender.SaveMagnified(pixels, stride, height, new Int32Rect(right - size + 2, cropY, size, size), 10, "corner-right-" + label);

        // Mirror the two corners along the frame itself: an L of the three rows nearest the edge
        // across the arc's seven columns, and the two columns nearest each side down the next
        // four rows, pane pixels only. Outside the pane the two sides differ by design: the
        // rail and tree sit on one side and the window's edge on the other, several themes
        // paint the window with one wide gradient, and the pane's drop shadow is covered on one
        // side only. The arc's cut-away shows that outside, so it is left out as well. Further
        // in is content, whose icons and grain are not mirror images of each other.
        double worst = 0; string where = "";

        // Device pixels per layout unit at this app scale and display scale: the 6px corner
        // radius and the sampled band grow with it.
        double k = (pane.PaneStack.TransformToAncestor(PaneRender.Screen(window)) as Transform)!.Value.M11 * dpi.DpiScaleX;
        double radius = 6 * k;
        int rows = (int)Math.Ceiling(7 * k), arcCols = (int)Math.Ceiling(7 * k), sideCols = (int)Math.Ceiling(3 * k);
        for (int dy = 1; dy <= rows; dy++)
            for (int dx = 0; dx < (dy <= Math.Ceiling(3 * k) ? arcCols : sideCols); dx++)
            {
                int y = edge + step * dy;
                if (y < 0 || y >= height) continue;
                if ((radius - dx) * (radius - dx) + (radius - dy) * (radius - dy) > 20 * k * k) continue;
                double l = PaneRender.Luma(pixels, stride, left + dx, y);
                double r = PaneRender.Luma(pixels, stride, right - 1 - dx, y);
                if (Math.Abs(l - r) > worst) { worst = Math.Abs(l - r); where = dx + " in, " + dy + " rows in: left " + l.ToString("0") + " right " + r.ToString("0"); }
            }
        return worst < 24 ? null : "the pane's corners differ at " + where
            + " (frame " + frame.Left.ToString("0.00") + ".." + frame.Right.ToString("0.00") + ", radius " + pane.ResultsPane.CornerRadius + ")";
    }
}

/// <summary>The connected monitors, and moving a window onto one by its device rectangle.</summary>
internal static class Monitors
{
    internal readonly struct Monitor(Rect work, double scale)
    {
        internal Rect Work { get; } = work;
        internal double Scale { get; } = scale;
        public override string ToString() => Work + " at " + (Scale * 100).ToString("0") + "%";
    }

    internal static System.Collections.Generic.List<Monitor> All()
    {
        var found = new System.Collections.Generic.List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(handle, ref info)) return true;
            uint dpi = 96;
            try { GetDpiForMonitor(handle, 0, out dpi, out _); } catch { }
            found.Add(new Monitor(new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), dpi / 96.0));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Puts the window's top-left 20 device pixels inside the monitor's work area and
    /// reports whether it is there. The monitor's scale is read from the window afterwards
    /// (VisualTreeHelper.GetDpi): GetDpiForMonitor answers 96 for every monitor to a host that
    /// is not DPI-aware, which the test runner's PowerShell is not.</summary>
    internal static bool MoveTo(Window window, Monitor monitor)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, (int)monitor.Work.Left + 20, (int)monitor.Work.Top + 20, 0, 0, 0x0001 | 0x0004 | 0x0010);
        return GetWindowRect(hwnd, out var r) && monitor.Work.Contains(new Point(r.Left + 1, r.Top + 1));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    private delegate bool MonitorProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc proc, IntPtr data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
