using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class IconViewClickScrollTests
{
    [Fact]
    public Task ClickingAnIconInAScrolledFolderKeepsTheView()
        => RuntimeComponentProcess.Run(nameof(IconViewClickScrollScenario));

    [Fact]
    public Task RefreshingTheSameFolderKeepsTheView()
        => RuntimeComponentProcess.Run(nameof(IconViewRefreshScrollScenario));
}

public static class IconViewRefreshScrollScenario
{
    // A folder that changes on disk, a click back into its pane or a view option re-lists the folder
    // in place (keepSelection). The list is cleared and refilled, and it must come back scrolled
    // where you left it, not at the top or at the old selection.
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        for (int i = 0; i < 400; i++) File.WriteAllText(Path.Combine(fixture.Root, "file" + i.ToString("D3") + ".txt"), "x");
        var pane = window.Pane;
        window.FocusPane(pane);
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement((Button)pane.FindName("ViewIconsBtn"))
            .GetPattern(PatternInterface.Invoke)).Invoke();
        var navigate = typeof(MainWindow).GetMethod("NavigateTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        navigate.Invoke(window, [fixture.Root, false, false, false]);
        var list = pane.ResultsList;
        ShellWindowFixture.PumpUntil(() => list.Items.Count >= 400, diagnostic: () => "items=" + list.Items.Count);
        ScrollViewer? scroller = null;
        ShellWindowFixture.PumpUntil(() => (scroller = Find<ScrollViewer>(list)) is { ScrollableHeight: > 1000 });
        list.SelectedIndex = 3;

        double target = scroller!.ScrollableHeight * 0.6;
        scroller.ScrollToVerticalOffset(target);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => System.Math.Abs(scroller.VerticalOffset - target) < 1);

        navigate.Invoke(window, [fixture.Root, false, true, true]);
        ShellWindowFixture.PumpUntil(() => pane.Active.StatusKey == "Str_Status_Listed" && list.Items.Count >= 400);
        for (int i = 0; i < 8; i++) ShellWindowFixture.PumpUntil(() => true);
        Assert.True(System.Math.Abs(scroller.VerticalOffset - target) < 1,
            "A refresh in place moved the view from " + target + " to " + scroller.VerticalOffset);
        Assert.Equal(3, list.SelectedIndex);
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}

public static class IconViewClickScrollScenario
{
    // Scroll a big folder in the icon view, select one item, scroll somewhere else and click an
    // item that is on screen: the view stays put instead of jumping back toward the old selection.
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        for (int i = 0; i < 400; i++) File.WriteAllText(Path.Combine(fixture.Root, "file" + i.ToString("D3") + ".txt"), "x");
        var pane = window.Pane;
        window.FocusPane(pane);
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement((Button)pane.FindName("ViewIconsBtn"))
            .GetPattern(PatternInterface.Invoke)).Invoke();
        typeof(MainWindow).GetMethod("NavigateTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [fixture.Root, false, false, false]);
        var list = pane.ResultsList;
        ShellWindowFixture.PumpUntil(() => list.Items.Count >= 400, diagnostic: () => "items=" + list.Items.Count);
        var scroller = Descendants(list).OfType<ScrollViewer>().First();
        ShellWindowFixture.PumpUntil(() => scroller.ScrollableHeight > 1000, diagnostic: () => "scrollable=" + scroller.ScrollableHeight);

        // Select something near the top first, the way a first click would.
        window.UpdateLayout();
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
        Click(first);

        foreach (double target in new[] { scroller.ScrollableHeight / 2, scroller.ScrollableHeight * 0.8, scroller.ScrollableHeight / 3 })
        {
            scroller.ScrollToVerticalOffset(target);
            window.UpdateLayout();
            ShellWindowFixture.PumpUntil(() => System.Math.Abs(scroller.VerticalOffset - target) < 1);
            double before = scroller.VerticalOffset;
            var onScreen = Descendants(list).OfType<ListBoxItem>()
                .Where(item => item.IsVisible && IsFullyInside(item, scroller))
                .OrderBy(item => item.TranslatePoint(new Point(0, 0), scroller).Y)
                .Skip(3).First();
            Click(onScreen);
            for (int i = 0; i < 8; i++) ShellWindowFixture.PumpUntil(() => true);
            Assert.True(System.Math.Abs(scroller.VerticalOffset - before) < 1,
                "Clicking " + onScreen.DataContext + " moved the view from " + before + " to " + scroller.VerticalOffset);
            Assert.True(onScreen.IsSelected, "The clicked icon must be the selected one.");
        }
    }

    private static void Click(ListBoxItem item)
    {
        var source = PresentationSource.FromVisual(item);
        foreach (var routed in new[] { UIElement.PreviewMouseLeftButtonDownEvent, UIElement.MouseLeftButtonDownEvent })
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, System.Environment.TickCount, MouseButton.Left) { RoutedEvent = routed, Source = item });
        item.Focus();
        foreach (var routed in new[] { UIElement.PreviewMouseLeftButtonUpEvent, UIElement.MouseLeftButtonUpEvent })
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, System.Environment.TickCount, MouseButton.Left) { RoutedEvent = routed, Source = item });
        ShellWindowFixture.PumpUntil(() => item.IsSelected || item.IsKeyboardFocusWithin);
    }

    private static bool IsFullyInside(FrameworkElement item, ScrollViewer scroller)
    {
        var top = item.TranslatePoint(new Point(0, 0), scroller).Y;
        return top >= 0 && top + item.ActualHeight <= scroller.ViewportHeight;
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
