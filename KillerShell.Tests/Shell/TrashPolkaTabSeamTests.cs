using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Controls;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Terminal;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// Trash Polka: the active tab and the page it opens are one surface. Every page that can be the active tab is
/// rendered in every accent, with the tabs on top and on the bottom, in one pane and in two, and the rows of
/// pixels across the join are read back. A border line, a shadow band or a step in brightness at the join fails.
/// </summary>
[Trait("Category", "Window")]
public sealed class TrashPolkaTabSeamTests
{
    internal const string CaseVariable = "KILLERSHELL_SEAM_CASE";

    public static IEnumerable<object[]> Cases()
        => from accent in Enum.GetNames(typeof(Accent))
           from placement in new[] { "top", "bottom" }
           from layout in new[] { "single", "dual" }
           select new object[] { accent, placement, layout };

    /// <summary>One fresh process per accent, placement and layout: the live tool pages pile up work in a long run,
    /// and the case is handed to the scenario through the environment the child process inherits.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public Task ActiveTabsJoinTheirPagesOnTrashPolka(string accent, string placement, string layout)
    {
        Environment.SetEnvironmentVariable(CaseVariable, $"{accent},{placement},{layout}");
        return RuntimeComponentProcess.Run(nameof(TrashPolkaTabSeamScenario), 180000);
    }

    /// <summary>The same join under an app size other than 100%, where the tab and the page sit in scaled units:
    /// every run above was at 100% and missed a seam that showed at the sizes people use.</summary>
    [Theory]
    [InlineData("Red", "bottom", "single", "1.1")]
    [InlineData("Red", "bottom", "dual", "1.1")]
    [InlineData("Red", "top", "single", "1.1")]
    [InlineData("Red", "top", "single", "1.25")]
    [InlineData("Orange", "bottom", "single", "1.25")]
    public Task ActiveTabsJoinTheirPagesAtAppSize(string accent, string placement, string layout, string scale)
    {
        Environment.SetEnvironmentVariable(CaseVariable, $"{accent},{placement},{layout},{scale}");
        return RuntimeComponentProcess.Run(nameof(TrashPolkaTabSeamScenario), 180000);
    }

    [Fact]
    public Task PerformancePageUsesTheListSurface()
        => RuntimeComponentProcess.Run(nameof(TrashPolkaPerformanceSurfaceScenario));

    /// <summary>The tab just switched away from wears the same see-through face as a tab that was never active, by
    /// a click and by Ctrl+Tab, straight after the switch and after a rest.</summary>
    [Fact]
    public Task OutgoingTabDropsTheActiveFace()
        => RuntimeComponentProcess.Run(nameof(TrashPolkaSwitchAwayScenario), 540000);
}

public static class TrashPolkaSwitchAwayScenario
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Orange);
        ThemeManager.Apply(Theme.TrashPolka);
        window.SetTabPlacement(false, persist: false);
        if (window.DualPane) window.ToggleDualPane();
        window.FocusPane(pane);
        // The fixture hides its window (Opacity 0); this check reads the composited screen, so it has to be drawn.
        window.Opacity = 1;
        window.Topmost = true;

        // As in Steve's shot: Screenshots is the first tab and the one switched away from, the branding folder beside
        // it the one switched to, and KillerMCP an inactive tab to compare the outgoing face with.
        var tabs = new Dictionary<string, SearchTab>();
        SearchTab OpenFolder(string name)
        {
            string folder = Path.Combine(fixture.Root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.txt"), name);
            int before = pane.Tabs.Count;
            window.OpenFolderTabLeft(folder);
            ShellWindowFixture.PumpUntil(() => pane.Tabs.Count > before && pane.Active?.Title == name);
            return pane.Active!;
        }
        foreach (var name in new[] { "Screenshots", "thekiller-branding", "KillerMCP" }) tabs[name] = OpenFolder(name);
        foreach (var extra in pane.Tabs.Where(t => !tabs.ContainsValue(t)).ToArray()) pane.Tabs.Remove(extra);
        // A new tab opens beside the active one, so the order is set here: Ctrl+Tab from Screenshots lands on its neighbour.
        void Order()
        {
            pane.Tabs.Move(pane.Tabs.IndexOf(tabs["Screenshots"]), 0);
            pane.Tabs.Move(pane.Tabs.IndexOf(tabs["thekiller-branding"]), 1);
        }
        Order();
        var switchTo = window.GetType().GetMethod("SwitchToTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cycle = window.GetType().GetMethod("CycleTab", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // A click goes through the strip's own press and release handlers, which switch on the release.
        void Click(SearchTab target)
        {
            var bd = MenuBarTabBlendScenario.TabBorder(pane, target);
            // The handlers read the real pointer, so it sits on the tab as it would for a click; anywhere else the
            // press reads as the start of a drag and the release tears the tab out.
            var p = bd.PointToScreen(new Point(bd.ActualWidth / 2, bd.ActualHeight / 2));
            SetCursorPos((int)p.X, (int)p.Y);
            Settle(window, 60);
            var press = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = bd };
            window.Tab_DragDown(bd, press);
            var dragTab = window.GetType().GetField("_tabDragTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
            // The move WPF raises as the capture is taken can read a stale press point and start a drag that a still
            // pointer never would; a click is a press and a release in place, so that flag goes back to rest.
            window.GetType().GetField("_tabDragging", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
            Assert.True(bd.IsMouseCaptured && ReferenceEquals(dragTab.GetValue(window), target), "The press did not take the tab.");
            var release = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = bd };
            window.Tab_DragUp(bd, release);
        }

        // Ctrl+Tab is the window's key handler calling CycleTab (MainWindow.xaml.cs).
        var outgoing = tabs["Screenshots"];
        var incoming = tabs["thekiller-branding"];
        var ways = new (string Name, Action Switch)[]
        {
            ("click", () => Click(incoming)),
            ("ctrltab", () => cycle.Invoke(window, new object[] { 1 })),
        };
        var failures = new List<string>();
        // Then again with more tabs than the strip holds, so the run of visible tabs and its edges move.
        foreach (bool overflow in new[] { false, true })
        {
            if (overflow)
            {
                for (int i = 1; i <= 6; i++) OpenFolder($"Extra{i}");
                Order();
                window.Width = 900;
                Settle(window, 400);
            }
            foreach (var (way, doSwitch) in ways)
                foreach (bool rest in new[] { false, true })
                {
                    switchTo.Invoke(window, new object[] { outgoing });
                    Order();
                    Settle(window, rest ? 800 : 30);
                    doSwitch();
                    Settle(window, rest ? 800 : 150);
                    string label = $"switch-away-{(overflow ? "overflow" : "plain")}-{way}-{(rest ? "rest" : "quick")}";
                    if (pane.Active != incoming)
                    {
                        failures.Add($"{label}: the switch never happened, {pane.Active?.Title} is active");
                        continue;
                    }
                    var others = pane.Tabs.Where(t => t.IsStripVisible && t != outgoing && t != incoming).ToArray();
                    string? failure = CheckFace(window, pane, outgoing, others,
                        $"{label} (shown {string.Join("|", pane.Tabs.Where(t => t.IsStripVisible).Select(t => t.Title))}, chevron {pane.TabOverflowBtn.Visibility})");
                    if (failure != null) failures.Add(failure);
                }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>The outgoing tab's face against the tabs that were never active: no ink layer, no fill, and the pixels
    /// across the face read within the spread clean inactive tabs show among themselves. A stale active face reads
    /// about 8 above a clean one (11.png: 18 against 10), while clean tabs differ by up to 6 with the ink under
    /// them, so the face is held to the median of every other shown inactive tab with a margin of 7.</summary>
    internal static string? CheckFace(Window window, FilePane pane, SearchTab outgoing, IReadOnlyList<SearchTab> others, string label)
    {
        var outBd = (TabSurfaceBorder)MenuBarTabBlendScenario.TabBorder(pane, outgoing);
        var otherBds = others.Select(t => (FrameworkElement)MenuBarTabBlendScenario.TabBorder(pane, t)).ToArray();
        var neverBd = (TabSurfaceBorder)otherBds[0];
        Border Named(Border bd, string name) => MenuBarTabBlendScenario.Descendants<Border>(bd).Single(b => b.Name == name);
        var ink = Named(outBd, "tabInk");
        var grain = Named(outBd, "tabGrain");
        string state = $"bg={Describe(outBd.Background)} menu={Describe(outBd.MenuBackground)} blend={outBd.BlendProgress:0.00} " +
                       $"ink={ink.Visibility}/{Describe(ink.Background)} grain={grain.Opacity:0.00} never-bg={Describe(neverBd.Background)}";

        var root = PaneRender.Screen(window);
        double scale = PaneRender.DeviceScale(window);
        var pixels = Render(window, out int width, out int height, out int stride);
        double Level(Border bd)
        {
            var face = PaneRender.Device(PaneRender.BoundsIn(bd, root), scale);
            var values = new List<double>();
            // The band above the icon and the title, clear of the stripe an active tab wears on top.
            for (int y = (int)face.Top + 4; y < (int)face.Top + 8; y++)
                for (int x = (int)face.Left + 12; x < (int)face.Right - 12; x++)
                    values.Add(PaneRender.Luma(pixels, stride, x, y));
            values.Sort();
            return values.Count == 0 ? 0 : values[values.Count / 2];
        }
        double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        }
        double outgoingLevel = Level(outBd), neverLevel = Median(otherBds.Select(b => Level((Border)b)));
        // The same band read off the composited window, which is what the screen shows: a face the logical tree has
        // dropped but the screen never repainted only shows up here.
        var (screenOut, screenOthers, screenSpread) = ScreenLevels(window, outBd, otherBds);
        double screenNever = Median(screenOthers);
        state += $" screen {screenOut:0}/{screenNever:0} spread {screenSpread:0}";
        bool clean = ink.Visibility != Visibility.Visible && outBd.Background is SolidColorBrush { Color.A: 0 }
                     && outgoingLevel - neverLevel < 7
                     && (screenSpread < 1 || screenOut - screenNever < 7);
        Console.WriteLine($"  {label}: outgoing {outgoingLevel:0} never {neverLevel:0}; {state}");
        if (Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            File.AppendAllText(Path.Combine(evidence, "switch-away.txt"),
                $"{label}: outgoing {outgoingLevel:0} never {neverLevel:0}; {state}{Environment.NewLine}");
        }
        if (clean) return null;
        var crop = PaneRender.Device(PaneRender.BoundsIn(pane.TabStrip, root), scale);
        PaneRender.SaveMagnified(pixels, stride, height,
            new Int32Rect((int)crop.Left, (int)crop.Top, Math.Min((int)crop.Width, width - (int)crop.Left), Math.Min((int)crop.Height + 8, height - (int)crop.Top)), 2, label);
        return $"{label}: outgoing face {outgoingLevel:0} against {neverLevel:0}; {state}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    private struct NativePoint { public int X, Y; }
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    /// <summary>Median luma of each tab's top band in the window's composited client area (PrintWindow with
    /// PW_CLIENTONLY | PW_RENDERFULLCONTENT), plus the spread across the whole capture so a blank one is known.</summary>
    private static (double Outgoing, double[] Others, double Spread) ScreenLevels(Window window, FrameworkElement outgoing, IEnumerable<FrameworkElement> others)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        GetClientRect(hwnd, out var client);
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0) return (0, Array.Empty<double>(), 0);
        IntPtr screen = GetDC(hwnd), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr old = SelectObject(memory, bitmap);
        byte[] pixels;
        try
        {
            PrintWindow(hwnd, memory, 3);
            SelectObject(memory, old);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            pixels = new byte[width * 4 * height];
            bgra.CopyPixels(pixels, width * 4, 0);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(hwnd, screen);
        }
        var origin = new NativePoint();
        ClientToScreen(hwnd, ref origin);
        double Band(FrameworkElement bd)
        {
            var topLeft = bd.PointToScreen(new Point(0, 0));
            var bottomRight = bd.PointToScreen(new Point(bd.ActualWidth, bd.ActualHeight));
            int left = (int)topLeft.X - origin.X, top = (int)topLeft.Y - origin.Y, right = (int)bottomRight.X - origin.X;
            var values = new List<double>();
            for (int y = Math.Max(0, top + 4); y < Math.Min(height, top + 8); y++)
                for (int x = Math.Max(0, left + 12); x < Math.Min(width, right - 12); x++)
                    values.Add(PaneRender.Luma(pixels, width * 4, x, y));
            values.Sort();
            return values.Count == 0 ? 0 : values[values.Count / 2];
        }
        double low = 255, high = 0;
        for (int i = 0; i < pixels.Length; i += 4 * 97)
        {
            double luma = PaneRender.Luma(pixels, width * 4, (i / 4) % width, (i / 4) / width);
            low = Math.Min(low, luma);
            high = Math.Max(high, luma);
        }
        return (Band(outgoing), others.Select(Band).ToArray(), high - low);
    }

    private static string Describe(Brush? brush) => brush switch
    {
        null => "null",
        SolidColorBrush s => s.Color.ToString(),
        _ => brush.GetType().Name,
    };

    private static void Settle(Window window, int milliseconds) => TrashPolkaTabSeamScenario.Settle(window, milliseconds);

    private static byte[] Render(Window window, out int width, out int height, out int stride)
        => TrashPolkaTabSeamScenario.Render(window, out width, out height, out stride);
}

public static class TrashPolkaPerformanceSurfaceScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Orange);
        ThemeManager.Apply(Theme.TrashPolka);
        window.OpenPerformanceMonitor();
        ShellWindowFixture.PumpUntil(() => pane.Active?.Perf != null);
        var monitor = pane.Active!.Perf!;
        var hardware = new PerformanceHardwareInfo("CPU", "RAM", "GPU", "Network", 8, 1, 1, 1000, [], ["Adapter"], []);
        typeof(PerformanceMonitorControl).GetMethod("BuildTiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(monitor, new object[] { hardware });
        window.UpdateLayout();

        // The page carries the lighter list surface and its ink itself, so it never shows the window through it;
        // the tiles keep the darker face.
        Assert.Equal(MotifPart.Window, ThemeMotif.GetPart(monitor));
        Assert.Equal("ListPaneBrush", ThemeMotif.GetBaseBrush(monitor));
        Assert.NotNull(monitor.Background);
        Assert.Same(Application.Current.TryFindResource("MenuBackgroundBrush"), monitor.Resources["MonitorCellBrush"]);
        var tiles = TrashPolkaTabSeamScenario.Logical(monitor).OfType<Border>()
            .Where(b => ThemeMotif.GetBaseBrush(b) == "MonitorCellBrush" && ThemeMotif.GetPart(b) == MotifPart.Window).ToArray();
        Assert.NotEmpty(tiles);
        Assert.All(tiles, tile => Assert.True(ThemeMotif.GetCell(tile)));
    }
}

public static class TrashPolkaTabSeamScenario
{
    public static void Run()
    {
        string[] parts = (Environment.GetEnvironmentVariable(TrashPolkaTabSeamTests.CaseVariable) ?? "Red,top,single").Split(',');
        var accent = (Accent)Enum.Parse(typeof(Accent), parts[0]);
        bool bottom = parts[1] == "bottom", dual = parts[2] == "dual";
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        ThemeManager.ApplyAccent(Theme.TrashPolka, accent);
        ThemeManager.Apply(Theme.TrashPolka);
        window.SetTabPlacement(bottom, persist: false);
        if (window.DualPane != dual) window.ToggleDualPane();
        double appScale = parts.Length > 3 ? double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
        if (appScale != 1.0) window.ApplyAppScale(appScale);
        window.FocusPane(pane);
        Settle(window, 500);

        // Every kind of page the active tab can open, each kept by reference so it can be brought back.
        var pages = new List<(string Name, SearchTab Tab)> { ("files", pane.Active!) };
        SearchTab Open(Action open, Func<SearchTab, bool> kind)
        {
            open();
            ShellWindowFixture.PumpUntil(() => pane.Active != null && kind(pane.Active));
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} opened {pane.Active!.Title}");
            return pane.Active!;
        }
        pages.Add(("terminal", Open(() => window.OpenShell(TerminalProfile.Cmd(), fixture.Root), t => t.Term != null)));
        string text = Path.Combine(fixture.Root, "seam.txt");
        File.WriteAllText(text, "seam\r\n");
        var editor = Open(() => window.OpenForEditing(text), t => t.Editor != null);
        pages.Add(("editor", editor));
        // A finished search, as the user runs it: its status row ("1 match") shows under the listing, so a bottom tab
        // meets that row and not the list surface.
        editor.Groups[0].Terms[0].Pattern = "seam";
        editor.CurrentFolder = fixture.Root;
        pane.RootPathBox.Text = fixture.Root;
        ShellWindowFixture.Click(window, "SearchButton");
        ShellWindowFixture.PumpUntil(() => !ReferenceEquals(pane.Active, editor) && pane.Active!.HasSearch
            && !pane.Active.IsSearching && pane.Active.Results.Count >= 1
            && pane.PaneStatusBar.Visibility == Visibility.Visible);
        pages.Add(("search", pane.Active!));
        pages.Add(("performance", Open(window.OpenPerformanceMonitor, t => t.Perf != null)));
        pages.Add(("processes", Open(window.OpenTaskManager, t => t.Procs != null)));
        pages.Add(("events", Open(window.OpenEventViewer, t => t.Events != null)));
        pages.Add(("registry", Open(window.OpenRegistryEditor, t => t.Registry != null)));
        pages.Add(("storage", Open(window.OpenStorageAnalyzer, t => t.Storage != null)));

        var switchTo = window.GetType().GetMethod("SwitchToTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var failures = new List<string>();
        foreach (var (name, tab) in pages)
        {
            switchTo.Invoke(window, new object[] { tab });
            // A page with more below runs on into a bottom tab on purpose (the list peek), so read the join with
            // every scroller at its end, where the tab meets the page surface itself.
            Settle(window, 300);
            foreach (var scroller in Visuals(pane).OfType<ScrollViewer>()) scroller.ScrollToEnd();
            pane.RefreshBottomTabPeek();
            string label = $"seam-{accent}-{(bottom ? "bottom" : "top")}-{(dual ? "dual" : "single")}{(appScale != 1.0 ? "-" + (int)(appScale * 100) : "")}-{name}".ToLowerInvariant();
            string? failure = Check(window, pane, tab, bottom, label);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {label} {failure ?? "ok"}");
            // The whole window as well, for the theme audit: ink strength, page against card tiers and the tab join
            // read by eye across every page, accent and placement. Evidence runs only.
            if (Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE") is { Length: > 0 })
            {
                var whole = Render(window, out int w, out int h, out int s);
                PaneRender.Save(BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, whole, s), label + "-window");
            }
            if (failure != null) failures.Add(failure);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Let the ink land: the raster layers are built in the background and repainted when they arrive. Background
    /// priority, because a live monitor keeps the dispatcher from ever going idle.
    /// </summary>
    internal static void Settle(Window window, int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(15);
        }
        window.UpdateLayout();
    }

    /// <summary>
    /// Reads the pixel rows across the join inside the tab's width, column by column, and takes the median over the
    /// columns, so an ink shape that happens to cross the join moves a few columns and not the answer. The tab side
    /// and the page side match, and no row jumps from its neighbour: a line or a shadow band shows as a jump, a
    /// different surface as a step. The frame is read only once two renders a moment apart agree, so a blend or a
    /// raster still landing is reported as unstable rather than as a seam.
    /// </summary>
    private static string? Check(Window window, FilePane pane, SearchTab tab, bool bottom, string label)
    {
        var root = PaneRender.Screen(window);
        double scale = PaneRender.DeviceScale(window);
        byte[]? pixels = null;
        int width = 0, height = 0, stride = 0, left = 0, right = 0, seam = 0;
        Rect face = default;
        bool stable = false;
        for (int attempt = 0; attempt < 12 && !stable; attempt++)
        {
            Settle(window, attempt == 0 ? 400 : 250);
            var tabBorder = MenuBarTabBlendScenario.TabBorder(pane, tab);
            face = PaneRender.Device(PaneRender.BoundsIn(tabBorder, root), scale);
            byte[] next = Render(window, out width, out height, out stride);
            left = (int)Math.Ceiling(face.Left) + 10;
            right = Math.Min(width - 1, (int)Math.Floor(face.Right) - 10);
            seam = (int)Math.Round(bottom ? face.Top : face.Bottom);
            if (pixels != null && pixels.Length == next.Length)
            {
                double change = 0;
                int count = 0;
                for (int y = Math.Max(0, seam - 10); y < Math.Min(height, seam + 10); y++)
                    for (int x = left; x < right; x++, count++)
                        change += Math.Abs(PaneRender.Luma(next, stride, x, y) - PaneRender.Luma(pixels, stride, x, y));
                stable = count > 0 && change / count < 1.0;
            }
            pixels = next;
        }

        // Positive offsets go into the page, negative ones into the tab.
        int into = bottom ? -1 : 1;
        int capturedSeam = seam, capturedHeight = height, capturedStride = stride;
        byte[] frame = pixels!;
        double At(int x, int offset)
            => PaneRender.Luma(frame, capturedStride, x, Math.Max(0, Math.Min(capturedHeight - 1, capturedSeam + offset * into)));
        var columns = Enumerable.Range(left, Math.Max(0, right - left)).ToArray();
        double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        }
        // A table row's text can run right down to the join. A seam spans the tab's whole width, so the columns a
        // glyph crosses near the join are left out rather than read as a lighter page. Grain never strays this far.
        double tabLevel = Median(columns.Select(x => Enumerable.Range(3, 4).Average(o => At(x, -o))));
        var clear = columns.Where(x => Enumerable.Range(1, 6).All(o => Math.Abs(At(x, o) - tabLevel) < 60)).ToArray();
        if (clear.Length >= columns.Length / 4) columns = clear;
        double step = Median(columns.Select(x =>
            Enumerable.Range(2, 4).Average(o => At(x, o)) - Enumerable.Range(3, 4).Average(o => At(x, -o))));
        // Signed, so the grain's pixel-to-pixel noise cancels out and only a change most columns share is left. Read
        // through the tab and up to two rows into the page: a line or a shadow at the join sits there, while deeper
        // page rows carry the page's own pattern (the LCD screen's scanlines).
        var jumps = Enumerable.Range(-6, 8).Select(o => (o, jump: Math.Abs(Median(columns.Select(x => At(x, o + 1) - At(x, o)))))).ToArray();
        double jump = jumps.Max(j => j.jump);
        if (stable && Math.Abs(step) < 6 && jump < 6) return null;

        SaveMagnified(frame, stride, width, height,
            new Int32Rect((int)face.Left - 24, (int)face.Top - 24, (int)face.Width + 48, (int)face.Height + 48), 4, label);
        string rows = string.Join(" ", Enumerable.Range(-6, 12).Select(o => $"{o}:{Median(columns.Select(x => At(x, o))):0}"));
        var ink = Visuals(MenuBarTabBlendScenario.TabBorder(pane, tab)).OfType<Border>().Single(b => b.Name == "tabInk");
        return $"{label}: {(stable ? "" : "UNSTABLE ")}step {step:0.0} jump {jump:0.0} rows {rows}; tab ink {ThemeMotif.GetBaseBrush(ink)} screen={ThemeMotif.GetScreen(ink)}";
    }

    internal static byte[] Render(Window window, out int width, out int height, out int stride)
    {
        var root = PaneRender.Screen(window);
        for (int pass = 0; pass < 3; pass++)
            root.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => root.UpdateLayout()));
        root.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
        double scale = PaneRender.DeviceScale(window);
        width = (int)Math.Ceiling(root.ActualWidth * scale);
        height = (int)Math.Ceiling(root.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void SaveMagnified(byte[] pixels, int stride, int width, int height, Int32Rect crop, int magnification, string name)
    {
        int x = Math.Max(0, crop.X), y = Math.Max(0, crop.Y);
        crop = new Int32Rect(x, y, Math.Min(crop.Width, width - x), Math.Min(crop.Height, height - y));
        PaneRender.SaveMagnified(pixels, stride, height, crop, magnification, name);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var d in Visuals(VisualTreeHelper.GetChild(root, i))) yield return d;
    }

    internal static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Logical(child)) yield return d;
    }
}
