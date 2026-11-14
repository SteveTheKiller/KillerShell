using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class StorageMenuLifetimeTests
{
    [Fact]
    public Task UnrelatedStorageChromeCannotReopenAPreviousNodesMenu()
        => RuntimeComponentProcess.Run(nameof(StorageMenuLifetimeScenario), 120000);
}

public static class StorageMenuLifetimeScenario
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "storage-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pathA = Path.Combine(root, "A.txt"), pathB = Path.Combine(root, "B.txt");
        File.WriteAllText(pathA, new string('a', 8192));
        File.WriteAllText(pathB, new string('b', 16384));
        var app = RuntimeComponentProcess.StartApplication();
        var storage = new StorageAnalyzerControl(root);
        Rect work = SystemParameters.WorkArea;
        var window = new Window
        {
            Content = storage, Title = "Storage menu lifetime regression", WindowStartupLocation = WindowStartupLocation.Manual,
            Left = work.Left + 16, Top = work.Top + 16,
            Width = Math.Min(900, work.Width - 32), Height = Math.Min(650, work.Height - 32),
            ShowActivated = true, ShowInTaskbar = false
        };
        string stage = "show visible owned Storage window";
        Exception? primaryError = null;
        try
        {
            window.Show();
            window.Activate();
            ShellWindowFixture.PumpUntil(() => window.IsActive && storage.IsLoaded && storage.IsVisible);
            Assert.True(work.Contains(new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)),
                "The entire test window must fit in the work area.");
            var target = Assert.Single(Visuals<TextBox>(storage));
            Pattern<IValueProvider>(target, PatternInterface.Value).SetValue(root);
            var scan = Assert.Single(Visuals<Button>(storage), item =>
                Equals(item.Content, window.FindResource("Str_Storage_Scan")));
            Scan(scan, storage, pathA, pathB);
            var failures = new List<string>();
            for (int round = 0; round < 2; round++)
            {
                stage = "round " + round + ": select actual A and open its Apps menu";
                var map = Map(storage);
                Select(map.Peer, pathA);
                Focus(map.Element);
                Stable(window, map.Element, pathA, pathB);
                Key(0x5D);
                WaitHeader(pathA);
                var selected = Assert.Single(Nodes(map.Peer), peer => peer.GetAutomationId() == pathA);
                var menu = Assert.Single(OpenMenus());
                Rect selectedBounds = selected.GetBoundingRectangle();
                Rect anchor = menu.PlacementRectangle;
                if (anchor.IsEmpty || !Near(map.Element.PointToScreen(anchor.TopLeft), selectedBounds.TopLeft)
                    || !Near(map.Element.PointToScreen(anchor.BottomRight), selectedBounds.BottomRight))
                    failures.Add("round " + round + ": keyboard menu is not anchored to selected A rectangle.");
                stage = "round " + round + ": close A through Escape";
                CloseMenu();
                if (round == 1)
                {
                    stage = "replace actual scan tree while A menu is closed";
                    Scan(scan, storage, pathA, pathB);
                    map = Map(storage);
                }
                stage = "round " + round + ": select actual B and right-click noninteractive footer text";
                Select(map.Peer, pathB);
                Focus(map.Element);
                var footer = Assert.Single(storage.Children.OfType<Grid>(), item => Grid.GetRow(item) == 3);
                TextBlock? text = null;
                Point point = default;
                ShellWindowFixture.PumpUntil(() =>
                {
                    foreach (var candidate in Visuals<TextBlock>(footer).Where(item => item.IsVisible && item.IsEnabled && !string.IsNullOrWhiteSpace(item.Text)))
                    {
                        Rect bounds = Bounds(candidate);
                        var center = new Point(Math.Round(bounds.Left + bounds.Width / 2), Math.Round(bounds.Top + bounds.Height / 2));
                        if (bounds.Contains(center) && WorkContains(window, center) && Hits(window, candidate, center))
                        { text = candidate; point = center; return true; }
                    }
                    return false;
                }, diagnostic: () => "No visible footer TextBlock has a safe rounded hit point.");
                Stable(window, map.Element, pathA, pathB, text!);
                Assert.False(Bounds(map.Element).Contains(point), "Footer point must be outside the treemap.");
                Assert.True(Hits(window, text!, point), "The exact rounded point must still hit footer text.");
                RightClick(window, text!, point);
                var observe = Stopwatch.StartNew();
                bool reopened = false;
                ShellWindowFixture.PumpUntil(() =>
                {
                    reopened |= OpenMenus().Any();
                    return observe.ElapsedMilliseconds >= 600;
                });
                if (reopened)
                {
                    string headers = string.Join(" | ", OpenMenus().SelectMany(HeaderPaths));
                    failures.Add("round " + round + ": unrelated footer reopened a visible context menu; headers=" + headers);
                    CloseMenu();
                }
                stage = "round " + round + ": right-click Scan without exposing a stale recycle action";
                Rect scanBounds = Bounds(scan);
                var scanPoint = new Point(Math.Round(scanBounds.Left + scanBounds.Width / 2),
                    Math.Round(scanBounds.Top + scanBounds.Height / 2));
                RightClick(window, scan, scanPoint);
                observe.Restart();
                reopened = false;
                ShellWindowFixture.PumpUntil(() =>
                {
                    reopened |= OpenMenus().Any();
                    return observe.ElapsedMilliseconds >= 600;
                });
                if (reopened)
                {
                    failures.Add("round " + round + ": Scan reopened the old node menu and exposed its recycle action.");
                    CloseMenu();
                }
                stage = "round " + round + ": companion Apps opens current B";
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
                Select(map.Peer, pathB);
                Focus(map.Element);
                Key(0x5D);
                WaitHeader(pathB);
                CloseMenu();
            }
            stage = "unrelated chrome must leave node menus closed before and after rescan";
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        catch (Exception error)
        {
            primaryError = error;
            throw new InvalidOperationException("Storage menu lifetime stage: " + stage, error);
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            foreach (Action cleanup in new Action[] { storage.Shutdown, window.Close, app.Shutdown,
                () => Directory.Delete(root, true) })
            {
                try { cleanup(); }
                catch (Exception error) { cleanupErrors.Add(error); }
            }
            if (cleanupErrors.Count > 0)
            {
                var cleanupError = new AggregateException("Storage fixture cleanup failed.", cleanupErrors);
                if (primaryError == null) throw cleanupError;
                Console.Error.WriteLine(cleanupError.ToString());
            }
        }
    }

    private static void Scan(Button scan, StorageAnalyzerControl storage, string a, string b)
    {
        ShellWindowFixture.PumpUntil(() => scan.IsLoaded && scan.IsVisible && scan.IsEnabled
            && Equals(scan.Content, scan.FindResource("Str_Storage_Scan")));
        Pattern<IInvokeProvider>(scan, PatternInterface.Invoke).Invoke();
        ShellWindowFixture.PumpUntil(() =>
        {
            var peer = Map(storage).Peer;
            return Equals(scan.Content, scan.FindResource("Str_Storage_Scan"))
                && Nodes(peer).Any(item => item.GetAutomationId() == a)
                && Nodes(peer).Any(item => item.GetAutomationId() == b);
        }, diagnostic: () => "Real scan must finish and expose both owned files through public node peers.");
    }

    private static (FrameworkElement Element, AutomationPeer Peer) Map(StorageAnalyzerControl storage)
    {
        var found = Visuals<FrameworkElement>(storage).Select(item =>
            (Element: item, Peer: UIElementAutomationPeer.CreatePeerForElement(item)))
            .Where(item => item.Peer != null && item.Peer.GetClassName() == "StorageTreemap").ToArray();
        return Assert.Single(found);
    }

    private static IEnumerable<AutomationPeer> Nodes(AutomationPeer parent)
    {
        foreach (var peer in parent.GetChildren() ?? new List<AutomationPeer>())
        {
            yield return peer;
            foreach (var child in Nodes(peer)) yield return child;
        }
    }

    private static void Select(AutomationPeer map, string path)
    {
        var node = Assert.Single(Nodes(map), peer => peer.GetAutomationId() == path);
        var selection = Assert.IsAssignableFrom<ISelectionItemProvider>(node.GetPattern(PatternInterface.SelectionItem));
        selection.Select();
        Assert.True(selection.IsSelected, "The real file node must become selected: " + path);
    }

    private static T Pattern<T>(UIElement item, PatternInterface pattern) where T : class
        => Assert.IsAssignableFrom<T>(UIElementAutomationPeer.CreatePeerForElement(item).GetPattern(pattern));

    private static void Focus(FrameworkElement map)
    {
        Assert.True(map.Focus());
        ShellWindowFixture.PumpUntil(() => map.IsKeyboardFocusWithin);
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        OwnForeground();
    }

    private static ContextMenu[] OpenMenus()
        => PresentationSource.CurrentSources.Cast<PresentationSource>().OfType<HwndSource>().ToArray()
            .Where(source => source.RootVisual != null && Owned(source.Handle))
            .SelectMany(source => Visuals<ContextMenu>(source.RootVisual))
            .Where(menu => menu.IsOpen && menu.IsVisible && PresentationSource.FromVisual(menu) != null)
            .Distinct().ToArray();

    // The card header names the item and puts its full path on the dim line under the name.
    private static IEnumerable<string> HeaderPaths(ContextMenu menu)
        => menu.Items.OfType<MenuItem>().Where(item => item.Tag as string == "cardhead")
            .Select(item => LogicalTexts(item.Header as DependencyObject).LastOrDefault())
            .Where(text => !string.IsNullOrEmpty(text))!;

    private static IEnumerable<string> LogicalTexts(DependencyObject? root)
    {
        if (root == null) yield break;
        if (root is TextBlock block) yield return block.Text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var text in LogicalTexts(child)) yield return text;
    }

    private static void WaitHeader(string expected)
    {
        ShellWindowFixture.PumpUntil(() => OpenMenus().Any(menu => HeaderPaths(menu).Contains(expected)),
            diagnostic: () => "Visible connected popup must display the selected full path " + expected);
        var menu = Assert.Single(OpenMenus());
        Assert.Equal(expected, Assert.Single(HeaderPaths(menu)));
    }

    private static void CloseMenu()
    {
        Assert.NotEmpty(OpenMenus());
        Key(0x1B);
        ShellWindowFixture.PumpUntil(() => OpenMenus().Length == 0,
            diagnostic: () => "Escape must close the actual popup before the next action.");
    }

    private static void Stable(Window window, FrameworkElement map, string a, string b, TextBlock? footer = null)
    {
        object[]? previous = null;
        var stable = Stopwatch.StartNew();
        ShellWindowFixture.PumpUntil(() =>
        {
            window.UpdateLayout();
            var peer = UIElementAutomationPeer.CreatePeerForElement(map);
            var nodes = Nodes(peer).Where(item => item.GetAutomationId() == a || item.GetAutomationId() == b).ToArray();
            if (nodes.Length != 2 || nodes.Any(item => item.GetBoundingRectangle().IsEmpty)) { stable.Restart(); return false; }
            var values = new List<object> { Bounds(window), Bounds(map), map.RenderSize,
                nodes.Single(item => item.GetAutomationId() == a).GetBoundingRectangle(),
                nodes.Single(item => item.GetAutomationId() == b).GetBoundingRectangle() };
            if (footer != null) { values.Add(Bounds(footer)); values.Add(footer.Text); }
            object[] current = values.ToArray();
            if (previous == null || !current.SequenceEqual(previous)) stable.Restart();
            previous = current;
            return stable.ElapsedMilliseconds >= 250;
        }, diagnostic: () => "Public window, map, file-node and footer geometry must remain stable over elapsed time.");
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static Rect Bounds(FrameworkElement item)
        => new Rect(item.PointToScreen(new Point()), item.PointToScreen(new Point(item.ActualWidth, item.ActualHeight)));

    private static bool WorkContains(Window window, Point point)
    {
        var source = (HwndSource)PresentationSource.FromVisual(window);
        return new MatrixTransform(source.CompositionTarget.TransformToDevice)
            .TransformBounds(SystemParameters.WorkArea).Contains(point);
    }

    private static bool Hits(Window window, FrameworkElement expected, Point screen)
    {
        var hit = window.InputHitTest(window.PointFromScreen(screen)) as DependencyObject;
        for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, expected)) return true;
        return false;
    }

    private static bool Owned(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out uint pid);
        return pid == (uint)Process.GetCurrentProcess().Id;
    }

    private static void OwnForeground()
        => Assert.True(Owned(GetForegroundWindow()), "Native input requires this scenario's foreground process.");

    private static void Key(byte code)
    {
        OwnForeground();
        uint flags = code == 0x5D ? 1u : 0u;
        keybd_event(code, 0, flags, UIntPtr.Zero);
        keybd_event(code, 0, flags | 2u, UIntPtr.Zero);
    }

    private static bool Near(Point actual, Point expected)
        => Math.Abs(actual.X - expected.X) < 2 && Math.Abs(actual.Y - expected.Y) < 2;

    private static void RightClick(Window window, FrameworkElement expected, Point point)
    {
        OwnForeground();
        Assert.True(Hits(window, expected, point));
        var pixel = new Pixel { X = checked((int)point.X), Y = checked((int)point.Y) };
        Assert.True(Owned(WindowFromPoint(pixel)), "Native right-click must hit this scenario's window.");
        Assert.True(SetCursorPos(pixel.X, pixel.Y));
        OwnForeground();
        Assert.True(Hits(window, expected, point));
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Pixel { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Pixel point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
