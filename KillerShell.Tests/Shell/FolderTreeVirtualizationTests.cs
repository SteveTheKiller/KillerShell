using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class FolderTreeVirtualizationTests
{
    [Fact]
    public Task LargeTreeRevealsOffscreenFoldersWithoutRealizingEveryRow()
        => RuntimeComponentProcess.Run(nameof(FolderTreeVirtualizationScenario));
}

public static class FolderTreeVirtualizationScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var tree = (TreeView)window.FindName("FolderTree");
        Assert.True(VirtualizingPanel.GetIsVirtualizing(tree));

        string driveRoot = Path.GetPathRoot(fixture.Root)!;
        window.GoToFolder(driveRoot);
        var pane = (FilePane)window.FindName("LeftPane");
        ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == driveRoot);
        ShellWindowFixture.Click(window, "BookmarksBtn");
        ShellWindowFixture.PumpUntil(() => tree.IsVisible && tree.ActualHeight > 100
            && tree.SelectedItem is FolderNode selected && selected.Path == driveRoot);

        var roots = (ObservableCollection<FolderNode>)tree.ItemsSource;
        var root = new FolderNode(fixture.Root, "Fixture root", false, isGroup: true);
        for (int index = 0; index < 100000; index++)
        {
            string name = "folder" + index.ToString("D5");
            root.Children.Add(new FolderNode(Path.Combine(fixture.Root, name), name, false));
        }
        var last = root.Children[root.Children.Count - 1];
        var branch = root.Children[50000];
        Directory.CreateDirectory(branch.Path);
        var load = branch.LoadChildrenAsync();
        ShellWindowFixture.PumpUntil(() => load.IsCompleted);
        load.GetAwaiter().GetResult();
        var retained = new FolderNode(Path.Combine(branch.Path, "cached"), "cached", false);
        branch.Children.Add(retained);
        branch.IsExpanded = true;
        roots.Clear();
        roots.Add(root);
        roots.Add(new FolderNode(Path.Combine(fixture.Root, "other"), "Other", false, isGroup: true));
        root.IsExpanded = true;
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => Descendants(tree).OfType<TreeViewItem>().Any());
        AssertBounded(tree);

        Reveal(window, last.Path);
        Assert.Same(last, tree.SelectedItem);
        Assert.True(last.IsSelected);
        AssertBounded(tree);

        Reveal(window, branch.Path);
        Assert.Same(branch, tree.SelectedItem);
        Assert.True(branch.IsExpanded);
        Assert.Contains(retained, branch.Children);
        Assert.False(last.IsSelected);
        AssertBounded(tree);

        Reveal(window, last.Path);
        Reveal(window, branch.Path);
        Assert.True(branch.IsExpanded);
        Assert.Contains(retained, branch.Children);
        AssertBounded(tree);

        var branchItem = Descendants(tree).OfType<TreeViewItem>()
            .Single(item => ReferenceEquals(item.DataContext, branch));
        branchItem.IsExpanded = false;
        ShellWindowFixture.PumpUntil(() => !branch.IsExpanded);
        branchItem.IsExpanded = true;
        ShellWindowFixture.PumpUntil(() => branch.Children.Count == 0);
        Assert.True(branch.IsExpanded);
        Assert.Same(branch, tree.SelectedItem);

        var scroll = Descendants(tree).OfType<ScrollViewer>().First();
        Assert.True(scroll.CanContentScroll);
        Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(tree));
        Assert.NotNull(scroll.Template.FindName("ViewportFadeHost", scroll));

        var rootItem = Descendants(tree).OfType<TreeViewItem>()
            .Single(item => ReferenceEquals(item.DataContext, root));
        var expander = (System.Windows.Controls.Primitives.ToggleButton)rootItem.Template.FindName("Expander", rootItem);
        Assert.All(Descendants(expander).OfType<System.Windows.Shapes.Path>(), arrow => Assert.Null(arrow.Fill));
        var upper = (FrameworkElement)rootItem.Template.FindName("VerLineTop", rootItem);
        var lower = (FrameworkElement)rootItem.Template.FindName("VerLineBottom", rootItem);
        double gap = lower.TranslatePoint(new Point(), rootItem).Y
            - upper.TranslatePoint(new Point(0, upper.ActualHeight), rootItem).Y;
        Assert.InRange(gap, 7.9, 8.1);
    }

    private static void Reveal(MainWindow window, string path)
    {
        var reveal = window.RevealInTree(path);
        ShellWindowFixture.PumpUntil(() => reveal.IsCompleted);
        reveal.GetAwaiter().GetResult();
        window.UpdateLayout();
        var tree = (TreeView)window.FindName("FolderTree");
        var item = Descendants(tree).OfType<TreeViewItem>()
            .Single(row => row.DataContext is FolderNode node && node.Path == path);
        var header = (FrameworkElement)item.Template.FindName("PART_Header", item);
        var scroll = Descendants(tree).OfType<ScrollViewer>().First();
        double top = header.TranslatePoint(new Point(), scroll).Y;
        Assert.True(top >= 0 && top + header.ActualHeight <= scroll.ActualHeight,
            "The selected folder header must be inside the visible tree viewport.");
    }

    private static void AssertBounded(TreeView tree)
        => Assert.InRange(Descendants(tree).OfType<TreeViewItem>().Count(), 1, 250);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
