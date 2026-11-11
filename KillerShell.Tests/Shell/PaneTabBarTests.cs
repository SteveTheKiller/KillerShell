using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class PaneTabBarRuleTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(7, true)]
    public void APaneShowsItsStripOnlyWithASecondTab(int tabs, bool shown)
        => Assert.Equal(shown, MainWindow.ShowsTabBar(tabs));
}

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class PaneTabBarTests
{
    [Fact]
    public Task EachPaneShowsItsOwnStripInDualPane()
        => RuntimeComponentProcess.Run(nameof(PaneTabBarScenario));

    [Fact]
    public Task ClosingADocumentShowsTheRemainingPerformanceTab()
        => RuntimeComponentProcess.Run(nameof(CloseDocumentScenario));
}

public static class PaneTabBarScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");

        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        Assert.Equal(Visibility.Collapsed, left.TabBar.Visibility);
        Assert.Equal(Visibility.Collapsed, right.TabBar.Visibility);

        // A second tab in the left pane shows the left strip only.
        window.FocusPane(left);
        left.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => left.Tabs.Count == 2,
            diagnostic: () => "left tabs=" + left.Tabs.Count);
        Assert.Equal(Visibility.Visible, left.TabBar.Visibility);
        Assert.Equal(Visibility.Collapsed, right.TabBar.Visibility);

        // Closing it hides the strip again.
        window.CloseTab_Click(new Button { Tag = left.Active }, new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => left.Tabs.Count == 1 && left.TabBar.Visibility == Visibility.Collapsed,
            diagnostic: () => "left tabs=" + left.Tabs.Count + "; bar=" + left.TabBar.Visibility);
    }
}

public static class CloseDocumentScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        string file = Path.Combine(fixture.Root, "notes.txt");
        File.WriteAllText(file, "notes");

        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        window.FocusPane(right);
        window.OpenPerformanceMonitor();
        window.OpenForEditing(file);
        ShellWindowFixture.PumpUntil(() => right.Active.Editor != null);

        // Closed from its own x while the OTHER pane has focus, which is the background close.
        window.FocusPane(left);
        var editorTab = right.Active;
        window.CloseTab_Click(new Button { Tag = editorTab }, new RoutedEventArgs(Button.ClickEvent));

        ShellWindowFixture.PumpUntil(() => !right.Tabs.Contains(editorTab)
                                        && right.Active.IsPerformanceMonitor
                                        && right.PerformanceHost.Visibility == Visibility.Visible
                                        && right.EditorHost.Visibility == Visibility.Collapsed,
            diagnostic: () => "tabs=" + right.Tabs.Count + "; perf=" + right.PerformanceHost.Visibility
                              + "; editor=" + right.EditorHost.Visibility);
    }
}
