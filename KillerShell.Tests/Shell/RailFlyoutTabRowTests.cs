using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

        // Tabs on top: the corner goes back to the pane's own foot.
        window.SetTabPlacement(bottom: false, persist: false);
        window.UpdateLayout();
        Assert.InRange(CardFoot(), HostFoot() - 8.5, HostFoot() - 7.5);
    }
}
