using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using KillerShell.Shell;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ContextTargetingTests
{
    [Fact]
    public Task KeyboardTreeCardTargetsFocusedFolderWhilePointerNamesAnotherFolder()
        => RuntimeComponentProcess.Run(nameof(KeyboardTreeTargetScenario));

    [Fact]
    public Task KeyboardRegistryCopyPathTargetsFocusedKeyWhilePointerNamesAnotherKey()
        => RuntimeComponentProcess.Run(nameof(KeyboardRegistryTargetScenario));
}

public static class KeyboardRegistryTargetScenario
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var previousClipboard = FileCommandCard.CaptureClipboard();
        bool copied = false;
        MainWindow.DemoMode = true;
        var registry = new RegistryEditorControl();
        var window = new Window
        {
            Content = registry, Width = 700, Height = 400,
            Left = SystemParameters.WorkArea.Left + 20, Top = SystemParameters.WorkArea.Top + 20,
            ShowInTaskbar = false
        };
        try
        {
            var tree = Descendants(registry).OfType<TreeView>().Single();
            var a = new RegistryNode("HKEY_CURRENT_USER", "Pointer A", isRoot: true);
            var b = new RegistryNode("HKEY_LOCAL_MACHINE", "Focused B", isRoot: true);
            tree.ItemsSource = new[] { a, b };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            var rowA = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(a);
            var rowB = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(b);
            rowB.IsSelected = true;
            rowB.Focus();
            var header = (FrameworkElement)rowA.Template.FindName("PART_Header", rowA);
            var point = header.PointToScreen(new Point(header.ActualWidth / 2, header.ActualHeight / 2));
            Assert.True(SetCursorPos((int)point.X, (int)point.Y));
            System.Windows.Input.Mouse.Synchronize();
            ShellWindowFixture.PumpUntil(() => rowB.IsKeyboardFocusWithin && rowA.IsMouseOver);
            keybd_event(0x5D, 0, 0, UIntPtr.Zero);
            keybd_event(0x5D, 0, 2, UIntPtr.Zero);
            ContextMenu? menu = null;
            ShellWindowFixture.PumpUntil(() =>
            {
                menu = PresentationSource.CurrentSources.OfType<HwndSource>()
                    .SelectMany(source => Descendants(source.RootVisual)).OfType<ContextMenu>().FirstOrDefault(candidate => candidate.IsOpen);
                return menu != null;
            });
            try
            {
                copied = true;
                menu!.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+C")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("HKEY_LOCAL_MACHINE", Clipboard.GetText());
                Assert.Same(b, tree.SelectedItem);
            }
            finally { menu!.IsOpen = false; }
        }
        finally
        {
            try { if (copied) FileCommandCard.RestoreClipboard(previousClipboard); }
            finally
            {
                registry.Shutdown();
                window.Close();
                MainWindow.DemoMode = false;
                app.Shutdown();
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}

public static class KeyboardTreeTargetScenario
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        window.WindowState = WindowState.Normal;
        window.Left = SystemParameters.WorkArea.Left + 20;
        window.Top = SystemParameters.WorkArea.Top + 20;
        window.Width = 950;
        window.Height = 650;
        window.ShowActivated = true;
        window.Activate();
        var tree = (TreeView)window.FindName("FolderTree");
        var treePanel = (FrameworkElement)window.FindName("TreePanel");
        if (treePanel.ActualWidth < 100) window.ToggleTreePanel();
        ShellWindowFixture.PumpUntil(() => treePanel.ActualWidth >= 160);
        var a = new FolderNode(fixture.Root, "Pointer folder A", false);
        var b = new FolderNode(fixture.Root, "Focused folder B", false);
        tree.ItemsSource = new[] { a, b };
        window.UpdateLayout();
        var rowA = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(a);
        var rowB = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(b);
        rowB.IsSelected = true;
        rowB.Focus();
        var header = (FrameworkElement)rowA.Template.FindName("PART_Header", rowA);
        Rect previousWindow = Rect.Empty, previousHeader = Rect.Empty;
        int stableSamples = 0;
        Point point = default;
        string readiness = string.Empty;
        ShellWindowFixture.PumpUntil(() =>
        {
            window.UpdateLayout();
            var windowBounds = new Rect(window.PointToScreen(new Point()),
                window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight)));
            var headerBounds = new Rect(header.PointToScreen(new Point()),
                header.PointToScreen(new Point(header.ActualWidth, header.ActualHeight)));
            stableSamples = windowBounds == previousWindow && headerBounds == previousHeader ? stableSamples + 1 : 0;
            previousWindow = windowBounds;
            previousHeader = headerBounds;
            var candidate = new Point(Math.Round(headerBounds.Left + headerBounds.Width / 2),
                Math.Round(headerBounds.Top + headerBounds.Height / 2));
            var monitor = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            Assert.True(GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(window).Handle, 2), ref monitor));
            var workArea = new Rect(monitor.Work.Left, monitor.Work.Top,
                monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
            var hit = window.InputHitTest(window.PointFromScreen(candidate)) as DependencyObject;
            bool targetsRow = false;
            for (var target = hit; target != null; target = VisualTreeHelper.GetParent(target))
                if (ReferenceEquals(target, rowA)) { targetsRow = true; break; }
            readiness = "window=" + windowBounds + "; header=" + headerBounds + "; samples=" + stableSamples
                + "; point=" + candidate + "; targetsRow=" + targetsRow;
            if (stableSamples < 5 || !workArea.Contains(candidate) || !windowBounds.Contains(candidate) || !targetsRow)
                return false;
            point = candidate;
            return true;
        }, diagnostic: () => readiness);
        Assert.True(SetCursorPos((int)point.X, (int)point.Y));
        System.Windows.Input.Mouse.Synchronize();
        ShellWindowFixture.PumpUntil(() => rowB.IsKeyboardFocusWithin && rowA.IsMouseOver,
            diagnostic: () => "active=" + window.IsActive + "; focused=" + rowB.IsKeyboardFocusWithin
                + "; mouse=" + rowA.IsMouseOver + "; point=" + point + "; header=" + header.ActualWidth + "," + header.ActualHeight
                + "; over=" + System.Windows.Input.Mouse.DirectlyOver);
        keybd_event(0x5D, 0, 0, UIntPtr.Zero);
        keybd_event(0x5D, 0, 2, UIntPtr.Zero);
        ShellWindowFixture.PumpUntil(() => tree.ContextMenu.IsOpen);
        try
        {
            var head = tree.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "cardhead");
            Assert.Equal("Focused folder B", AutomationProperties.GetName(head));
            Assert.Same(b, tree.SelectedItem);
        }
        finally { tree.ContextMenu.IsOpen = false; }
        ShellWindowFixture.PumpUntil(() => rowA.IsMouseOver);
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
        ShellWindowFixture.PumpUntil(() => tree.ContextMenu.IsOpen);
        try
        {
            var head = tree.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "cardhead");
            Assert.Equal("Pointer folder A", AutomationProperties.GetName(head));
            Assert.Same(a, tree.SelectedItem);
        }
        finally { tree.ContextMenu.IsOpen = false; }
    }
}
