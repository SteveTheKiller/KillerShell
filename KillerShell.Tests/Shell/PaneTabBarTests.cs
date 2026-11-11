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

    // One process per way of closing: the fixture's Application can only exist once per process.
    [Theory]
    [InlineData(nameof(CloseFromFocusedPaneScenario))]
    [InlineData(nameof(CloseDocumentBeforePerformanceScenario))]
    [InlineData(nameof(CloseWithEditorFocusedScenario))]
    [InlineData(nameof(CloseInLeftPaneFromRightScenario))]
    [InlineData(nameof(CloseInFocusedLeftPaneScenario))]
    public Task ClosingADocumentShowsTheRemainingPerformanceTabEveryWay(string scenario)
        => RuntimeComponentProcess.Run(scenario);

    // The left pane's ONLY tab closing folds the window to one pane holding the right pane's
    // tabs, the same as dragging that tab out does, instead of opening a blank tab there.
    [Theory]
    [InlineData(nameof(CloseLastLeftTabFromLeftScenario))]
    [InlineData(nameof(CloseLastLeftTabFromRightScenario))]
    public Task ClosingTheLeftPanesLastTabKeepsTheRightPanesTabs(string scenario)
        => RuntimeComponentProcess.Run(scenario);
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

public static class CloseFromFocusedPaneScenario { public static void Run() => CloseDocumentVariants.Variant("foreground"); }
public static class CloseDocumentBeforePerformanceScenario { public static void Run() => CloseDocumentVariants.Variant("editor-first"); }
public static class CloseWithEditorFocusedScenario { public static void Run() => CloseDocumentVariants.Variant("editor-focused"); }
public static class CloseInLeftPaneFromRightScenario { public static void Run() => CloseDocumentVariants.Variant("left-background"); }
public static class CloseInFocusedLeftPaneScenario { public static void Run() => CloseDocumentVariants.Variant("left-foreground"); }

internal static class CloseDocumentVariants
{
    internal static void Variant(string variant)
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        string file = Path.Combine(fixture.Root, "notes.txt");
        File.WriteAllText(file, "notes");

        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        var owner = variant.StartsWith("left") ? left : right;
        var other = owner == left ? right : left;
        window.FocusPane(owner);
        if (variant == "editor-first")
        {
            window.OpenForEditing(file);
            window.OpenPerformanceMonitor();
            window.OpenForEditing(file);   // switches back to the document
        }
        else
        {
            window.OpenPerformanceMonitor();
            window.OpenForEditing(file);
        }
        ShellWindowFixture.PumpUntil(() => owner.Active.Editor != null);
        var editorTab = owner.Active;
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => owner.TabStrip.ItemContainerGenerator.ContainerFromItem(editorTab) is FrameworkElement);

        if (variant == "editor-focused") editorTab.Editor!.TextArea.Focus();
        if (variant is "left-background" or "foreground" or "editor-first" or "editor-focused")
            window.FocusPane(variant == "left-background" ? other : owner);

        window.CloseTab_Click(new Button { Tag = editorTab }, new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => !owner.Tabs.Contains(editorTab)
                                        && owner.Active.IsPerformanceMonitor
                                        && owner.PerformanceHost.Visibility == Visibility.Visible
                                        && ReferenceEquals(owner.PerformanceSlot.Content, owner.Active.Perf)
                                        && owner.EditorHost.Visibility == Visibility.Collapsed
                                        && owner.TabFadeGhost.Visibility != Visibility.Visible,
            diagnostic: () => "active=" + owner.Active.Title + "; tabs=" + owner.Tabs.Count
                              + "; perf=" + owner.PerformanceHost.Visibility + "; editor=" + owner.EditorHost.Visibility
                              + "; ghost=" + owner.TabFadeGhost.Visibility + "; list=" + owner.ResultsList.Visibility);
    }
}

public static class CloseLastLeftTabFromLeftScenario { public static void Run() => CloseLastLeftTab.Run(focusLeft: true); }
public static class CloseLastLeftTabFromRightScenario { public static void Run() => CloseLastLeftTab.Run(focusLeft: false); }

internal static class CloseLastLeftTab
{
    internal static void Run(bool focusLeft)
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        string file = Path.Combine(fixture.Root, "notes.txt");
        File.WriteAllText(file, "notes");

        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);

        // Right: a folder and Performance, with Performance showing.
        window.FocusPane(right);
        window.OpenPerformanceMonitor();
        var perf = right.Active;
        Assert.True(perf.IsPerformanceMonitor);

        // Left: just the document.
        window.FocusPane(left);
        window.OpenForEditing(file);
        var editorTab = left.Active;
        window.CloseTab_Click(new Button { Tag = left.Tabs[0] }, new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => left.Tabs.Count == 1 && ReferenceEquals(left.Tabs[0], editorTab));

        if (!focusLeft) window.FocusPane(right);
        window.CloseTab_Click(new Button { Tag = editorTab }, new RoutedEventArgs(Button.ClickEvent));

        ShellWindowFixture.PumpUntil(() => !window.DualPane
                                        && ReferenceEquals(window.Pane, left)
                                        && ReferenceEquals(left.Active, perf)
                                        && left.Tabs.Count == 2
                                        && right.Tabs.Count == 0
                                        && left.PerformanceHost.Visibility == Visibility.Visible
                                        && ReferenceEquals(left.PerformanceSlot.Content, perf.Perf)
                                        && left.EditorHost.Visibility == Visibility.Collapsed,
            diagnostic: () => "dual=" + window.DualPane + "; left=" + string.Join(",", System.Linq.Enumerable.Select(left.Tabs, t => t.Title))
                              + "; active=" + left.Active?.Title + "; right=" + right.Tabs.Count
                              + "; perf=" + left.PerformanceHost.Visibility + "; editor=" + left.EditorHost.Visibility);
    }
}
