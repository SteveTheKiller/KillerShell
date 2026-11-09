using System.Collections.Generic;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

// Opening a file that is already in an editor tab goes to that tab, even when it sits in the
// second pane while that pane is hidden. A second tab over the same file is how one save
// silently overwrites the other.
public sealed class OpenEditorLookupTests
{
    private sealed class Tab(string? path)
    {
        public string? Path { get; } = path;
    }

    private sealed class Pane(string name, bool shown, List<Tab> tabs)
    {
        public string Name { get; } = name;
        public bool Shown { get; } = shown;
        public List<Tab> Tabs { get; } = tabs;
    }

    private static readonly Pane Left = new("left", true, [new Tab(@"C:\a.txt"), new Tab(null)]);
    private static readonly Pane HiddenRight = new("right", false, [new Tab(@"C:\Notes\todo.md")]);

    [Fact]
    public void AFileInTheHiddenPaneIsFound()
    {
        var hit = OpenEditorLookup.Find(new[] { Left, HiddenRight }, p => p.Shown, p => p.Tabs, t => t.Path, @"c:\notes\TODO.md");
        Assert.NotNull(hit);
        Assert.Same(HiddenRight, hit!.Value.Pane);
        Assert.Same(HiddenRight.Tabs[0], hit.Value.Tab);
    }

    [Fact]
    public void AFileNotOpenAnywhereIsNotFound()
        => Assert.Null(OpenEditorLookup.Find(new[] { Left, HiddenRight }, p => p.Shown, p => p.Tabs, t => t.Path, @"C:\b.txt"));
}
