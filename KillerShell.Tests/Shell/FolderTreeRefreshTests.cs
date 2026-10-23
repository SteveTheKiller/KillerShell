using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class FolderTreeRefreshTests
{
    [Fact]
    public async Task LargeUnchangedFolderRefreshPreservesNodesWithoutBlocking()
    {
        var parent = new FolderNode(@"C:\fixture", "fixture", false);
        var fresh = new List<FolderNode>();
        for (int i = 0; i < 100000; i++)
        {
            string name = i.ToString("D6");
            parent.Children.Add(new FolderNode(parent.Path + "\\" + name, name, false));
            fresh.Add(new FolderNode(parent.Path + "\\" + name, name, false));
        }
        var selected = parent.Children[75000];
        selected.IsSelected = true;
        selected.IsExpanded = true;
        int notifications = 0;
        parent.Children.CollectionChanged += (_, _) => notifications++;

        var elapsed = Stopwatch.StartNew();
        await parent.ReconcileChildrenAsync(fresh, deep: false);

        Assert.True(elapsed.ElapsedMilliseconds < 2000,
            "An unchanged 100,000-folder refresh blocked for " + elapsed.ElapsedMilliseconds + " ms.");
        Assert.Equal(0, notifications);
        Assert.Same(selected, parent.Children[75000]);
        Assert.True(selected.IsSelected);
        Assert.True(selected.IsExpanded);
    }

    [Fact]
    public async Task ChangedFolderRefreshKeepsSurvivorsAndSelectionWithoutReset()
    {
        var parent = new FolderNode(@"C:\fixture", "fixture", false);
        var removed = new FolderNode(@"C:\fixture\gone", "gone", false);
        var selected = new FolderNode(@"C:\fixture\keep", "keep", false) { IsSelected = true, IsExpanded = true };
        var moved = new FolderNode(@"C:\fixture\move", "move", false);
        parent.Children.Add(removed);
        parent.Children.Add(selected);
        parent.Children.Add(moved);
        bool reset = false;
        parent.Children.CollectionChanged += (_, e) => reset |= e.Action == NotifyCollectionChangedAction.Reset;

        await parent.ReconcileChildrenAsync(new[]
        {
            new FolderNode(moved.Path, moved.Name, false),
            new FolderNode(@"C:\fixture\new", "new", false),
            new FolderNode(selected.Path, selected.Name, false)
        }, deep: false);

        Assert.Equal(new[] { "move", "new", "keep" }, parent.Children.Select(n => n.Name));
        Assert.Same(moved, parent.Children[0]);
        Assert.Same(selected, parent.Children[2]);
        Assert.True(selected.IsSelected);
        Assert.True(selected.IsExpanded);
        Assert.False(reset);
    }
}
