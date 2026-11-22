using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Controls;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Trait("Category", "Window")]
public sealed class RailFlyoutTabRowTests
{
    [Fact]
    public Task CornerFlyoutsSitAboveTheBottomTabRow()
        => RuntimeComponentProcess.Run(nameof(RailFlyoutTabRowScenario));
}

/// <summary>
/// The rail flyouts' corner is eight pixels above the pane's visible foot. With the tabs below a
/// pane that has more than one tab, that foot is the tab row's top, so the menu no longer covers
/// the tabs. The placement callback is called directly, so no popup window opens.
/// </summary>
public static class RailFlyoutTabRowScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        var host = (FrameworkElement)window.FindName("PaneHost");
        var root = (FrameworkElement)window.Content;

        // The visible card's foot in root DIPs, for a small menu in the left corner.
        double CardFoot()
        {
            window.UpdateLayout();
            FlyoutPlacement.UsePane(host, SidebarSide.Left);
            var menu = new ContextMenu();
            FlyoutPlacement.Attach(menu, null);
            Thickness halo = menu.Template?.FindName("MenuRoot", menu) is FrameworkElement card ? card.Margin : new Thickness(22, 18, 22, 26);
            double sy = VisualTreeHelper.GetDpi(root).DpiScaleY;
            var size = new Size(240 * sy, 160 * sy);
            CustomPopupPlacement[] placed = menu.CustomPopupPlacementCallback(size, new Size(root.ActualWidth * sy, root.ActualHeight * sy), new Point());
            return (placed[0].Point.Y + size.Height - halo.Bottom * sy) / sy;
        }
        double HostFoot() => host.TransformToAncestor(root).TransformBounds(new Rect(host.RenderSize)).Bottom;
        double TabTop() => pane.TabBar.TransformToAncestor(root).TransformBounds(new Rect(pane.TabBar.RenderSize)).Top;

        // One tab and the tabs below: the bar is hidden, so the corner keeps the pane's foot.
        window.SetTabPlacement(bottom: true, persist: false);
        window.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, pane.TabBar.Visibility);
        Assert.InRange(CardFoot(), HostFoot() - 8.5, HostFoot() - 7.5);

        // Two tabs below: the card's foot is eight pixels above the tab row.
        window.FocusPane(pane);
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.Tabs.Count == 2 && pane.TabBar.Visibility == Visibility.Visible);
        window.UpdateLayout();
        Assert.True(TabTop() < HostFoot() - 10, "The tab row must sit at the pane's foot: tab top " + TabTop() + ", foot " + HostFoot());
        double above = CardFoot();
        Assert.InRange(above, TabTop() - 8.5, TabTop() - 7.5);
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE")))
        {
            FlyoutPlacement.SetVisibleFoot(host, null);
            Evidence(window, root, host, "flyout-over-tabs");
            FlyoutPlacement.SetVisibleFoot(host, window.BottomTabsTop);
            Evidence(window, root, host, "flyout-above-tabs");
        }

        // Tabs on top: the corner goes back to the pane's own foot.
        window.SetTabPlacement(bottom: false, persist: false);
        window.UpdateLayout();
        Assert.InRange(CardFoot(), HostFoot() - 8.5, HostFoot() - 7.5);
    }

    /// <summary>The window with a corner menu drawn where the placement puts it: the menu is laid out and rendered
    /// on its own, never opened, and composited over the window's pixels. The pane's lower left goes to the
    /// evidence folder.</summary>
    private static void Evidence(MainWindow window, FrameworkElement root, FrameworkElement host, string name)
    {
        // A flyout card like the rail's own menus, in a popup that is never opened.
        var rows = new StackPanel { Margin = new Thickness(6) };
        foreach (string label in new[] { "Dark", "Light", "Decay", "Win98", "Trash Polka" })
            rows.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12, 5, 40, 5) });
        var face = new Border { Style = (Style)Application.Current.FindResource("FlyoutCard"), Child = rows };
        var popup = new Popup { Child = face };
        FlyoutPlacement.UsePane(host, SidebarSide.Left);
        FlyoutPlacement.Attach(popup, null);
        face.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        face.Arrange(new Rect(face.DesiredSize));
        face.UpdateLayout();

        byte[] pixels = PaneRender.Pixels(window, name + "-window", out int width, out int height, out int stride);
        double scale = PaneRender.DeviceScale(window);
        var size = new Size(face.DesiredSize.Width * scale, face.DesiredSize.Height * scale);
        Point at = popup.CustomPopupPlacementCallback(size, new Size(width, height), new Point())[0].Point;

        var card = new RenderTargetBitmap((int)System.Math.Ceiling(size.Width), (int)System.Math.Ceiling(size.Height), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        card.Render(face);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride), new Rect(0, 0, width, height));
            context.DrawImage(card, new Rect(at.X, at.Y, card.PixelWidth, card.PixelHeight));
        }
        var shot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        shot.Render(visual);
        var composite = new byte[stride * height];
        shot.CopyPixels(composite, stride, 0);

        Rect pane = PaneRender.Device(host.TransformToAncestor(root).TransformBounds(new Rect(host.RenderSize)), scale);
        int cropHeight = (int)System.Math.Min(pane.Height, size.Height + 120);
        PaneRender.SaveMagnified(composite, stride, height,
            new Int32Rect(System.Math.Max(0, (int)pane.Left - 60), (int)(pane.Bottom - cropHeight + 24), (int)System.Math.Min(pane.Width, 640) + 60, cropHeight), 1, name);
    }
}
