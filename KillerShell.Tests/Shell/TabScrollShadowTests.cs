using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Trait("Category", "Window")]
public sealed class TabScrollShadowTests
{
    [Fact]
    public Task BottomTabsCarryTheListShadowAsOneFade()
        => RuntimeComponentProcess.Run(nameof(TabScrollShadowScenario));

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
        ShellWindowFixture.PumpUntil(() => pane.HasBottomTabShadow);
        // Part way down a row, so the list's foot cuts through a line of icons the way a real
        // folder does more often than not.
        var scroller = PaneRender.FindScroller(pane.ResultsList)!;
        scroller.ScrollToVerticalOffset(70);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => scroller.VerticalOffset > 60);
        ShellWindowFixture.PumpUntil(() => pane.TabScrollShadow.Clip != null && pane.TabScrollShadow.ActualHeight > 28);

        var tabFace = MenuBarTabBlendScenario.TabBorder(pane, pane.Active);
        Rect face = PaneRender.BoundsIn(tabFace, PaneRender.Screen(window));
        Rect list = PaneRender.BoundsIn(pane.ResultsPane, PaneRender.Screen(window));
        Rect overlay = PaneRender.BoundsIn(pane.TabScrollShadow, PaneRender.Screen(window));
        Assert.InRange(overlay.Top, list.Bottom - 29, list.Bottom - 27);
        Assert.InRange(overlay.Bottom, face.Bottom - 1, face.Bottom + 1);
        double scale = PaneRender.DeviceScale(window);
        face = PaneRender.Device(face, scale);
        list = PaneRender.Device(list, scale);
        overlay = PaneRender.Device(overlay, scale);

        byte[] shaded = PaneRender.Pixels(window, "shadow-window", out int width, out int height, out int stride);
        pane.TabScrollShadow.Visibility = Visibility.Collapsed;
        byte[] plain = PaneRender.Pixels(window, "shadow-window-plain", out _, out _, out _);
        pane.TabScrollShadow.Visibility = Visibility.Visible;

        double Shade(int px, int py) => PaneRender.Luma(shaded, stride, px, py) / Math.Max(1, PaneRender.Luma(plain, stride, px, py));

        int x = (int)(face.Left + face.Width / 2);
        int top = (int)Math.Ceiling(overlay.Top), seam = (int)Math.Round(list.Bottom), foot = (int)Math.Floor(face.Bottom) - 1;
        PaneRender.SaveMagnified(shaded, stride, height,
            new Int32Rect((int)face.Left - 12, top - 6, (int)face.Width + 24, foot - top + 12), 4, "shadow-join");
        PaneRender.SaveMagnified(shaded, stride, height, new Int32Rect((int)list.Left, top - 40, (int)list.Width, foot - top + 48), 2, "shadow-foot");

        // The shade (shaded over unshaded brightness) deepens down the column and never jumps
        // back up at the seam: that is what makes the list and the tab one surface.
        double above = Shade(x, top + 2), atSeamList = Shade(x, seam - 2), atSeamTab = Shade(x, seam + 2), atFoot = Shade(x, foot);
        Assert.True(above > 0.97, "The fade must start transparent 28px above the band: " + above);
        Assert.True(atSeamList < 0.9, "The list's foot must be shaded before the tab: " + atSeamList);
        Assert.True(Math.Abs(atSeamList - atSeamTab) < 0.08, "A line at the seam: list " + atSeamList.ToString("0.00") + " vs tab " + atSeamTab.ToString("0.00"));
        Assert.True(atFoot < atSeamTab - 0.05, "The tab's foot must be darker than its join: " + atFoot.ToString("0.00") + " vs " + atSeamTab.ToString("0.00"));

        // The inactive tab stays unshaded: sampled on its face, clear of icon and title.
        var other = pane.Tabs.First(t => t != pane.Active);
        Rect otherFace = PaneRender.Device(PaneRender.BoundsIn(MenuBarTabBlendScenario.TabBorder(pane, other), PaneRender.Screen(window)), scale);
        int ox = (int)(otherFace.Left + otherFace.Width * 0.72), oy = (int)(otherFace.Top + 3);
        Assert.InRange(Math.Abs(PaneRender.Luma(shaded, stride, ox, oy) - PaneRender.Luma(plain, stride, ox, oy)), 0, 2);

        // The folder carries on into its tab: the row cut off at the list's foot shows through
        // the active tab's face (tabListPeek) and that layer fades out before the tab's foot.
        var peek = PaneRender.FindNamedBorder(tabFace, "tabListPeek")!;
        Assert.Equal(Visibility.Visible, peek.Visibility);
        var brush = Assert.IsType<VisualBrush>(peek.Background);
        Assert.True(brush.Viewbox.Y > 0, "The peek must look just below the viewport.");
        Assert.Equal(brush.Viewbox.Width, brush.Viewport.Width);
        // Rendered: the white paper icons reach into the upper part of the tab's face.
        double brightest = 0;
        for (int py = seam + 2; py < seam + face.Height * 0.4; py++)
            for (int px = (int)face.Left + 2; px < face.Right - 2; px++)
                brightest = Math.Max(brightest, PaneRender.Luma(shaded, stride, px, py));
        Assert.True(brightest > 120, "The cut-off icons must show through the tab's face: brightest " + brightest.ToString("0"));

        // Scrolled to the end, the shadow is gone.
        scroller.ScrollToEnd();
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => !pane.HasBottomTabShadow);
        Assert.Equal(Visibility.Collapsed, pane.TabScrollShadow.Visibility);
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
