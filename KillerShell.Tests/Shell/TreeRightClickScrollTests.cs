using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Shell;
using Xunit;
using FolderNode = KillerShell.Shell.FolderNode;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TreeRightClickScrollTests
{
    [Fact]
    public Task RefreshingTheOpenFolderLeavesTheTreeWhereYouScrolledIt()
        => RuntimeComponentProcess.Run(nameof(TreeRefreshScrollScenario));
}

public static class TreeRefreshScrollScenario
{
    // Scroll the folder tree away from the open folder, then let that folder refresh in place (it
    // changed on disk, its pane took focus again, a view option changed). The tree stays where you
    // scrolled it instead of jumping back to the open folder.
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        window.Height = 480;
        var tree = (TreeView)window.FindName("FolderTree");
        var panel = (FrameworkElement)window.FindName("TreePanel");
        if (panel.ActualWidth < 100) window.ToggleTreePanel();
        ShellWindowFixture.PumpUntil(() => panel.ActualWidth >= 160);
        // The open folder sits under 80 siblings of its own, so the tree has room to scroll on any
        // machine. A tree that is short on its own (one drive, a shallow profile) would otherwise
        // never scroll far enough to tell a jump from staying put.
        for (int i = 0; i < 80; i++) System.IO.Directory.CreateDirectory(System.IO.Path.Combine(fixture.Root, "f" + i.ToString("00")));
        string open = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(fixture.Root, "zz-open")).FullName;
        // The fixture sits under the profile's temp folder, and AppData is hidden: the tree only
        // reaches it with hidden folders shown (the settings here are the fixture's own).
        if (!MainWindow.TreeShowHidden) window.ToggleTreeHidden();
        ShellWindowFixture.PumpUntil(() => MainWindow.TreeShowHidden);
        var navigate = typeof(MainWindow).GetMethod("NavigateTo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        navigate.Invoke(window, [open, true, false, false]);
        ShellWindowFixture.PumpUntil(() => window.Pane.Active.StatusKey == "Str_Status_Listed");
        _ = window.RevealInTree(open);
        ScrollViewer? scroller = null;
        ShellWindowFixture.PumpUntil(() =>
        {
            window.UpdateLayout();
            scroller = Descendants(tree).OfType<ScrollViewer>().FirstOrDefault();
            return scroller is { VerticalOffset: > 800 }
                && Descendants(tree).OfType<TreeViewItem>().Any(c => c.DataContext is FolderNode { IsSelected: true });
        }, diagnostic: () => "offset=" + scroller?.VerticalOffset);

        double target = scroller!.VerticalOffset - 600;
        scroller.ScrollToVerticalOffset(target);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => Math.Abs(scroller.VerticalOffset - target) < 1);

        navigate.Invoke(window, [open, false, true, true]);
        ShellWindowFixture.PumpUntil(() => window.Pane.Active.StatusKey == "Str_Status_Listed");
        for (int i = 0; i < 10; i++) ShellWindowFixture.PumpUntil(() => true);
        Assert.True(Math.Abs(scroller.VerticalOffset - target) < 1,
            "A refresh in place scrolled the tree from " + target + " to " + scroller.VerticalOffset);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
