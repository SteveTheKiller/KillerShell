using System.Collections;
using System.Collections.Generic;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

// A file that grows or is touched while the folder is open gets a fresh row for its new size
// and date. A row the user had selected stays selected through that swap.
public sealed class RowReplaceTests
{
    [Fact]
    public void ASelectedRowStaysSelectedWhenItIsReplaced()
    {
        var rows = new List<string> { "a", "b", "c" };
        IList selected = new ArrayList { "b" };

        RowReplace.Replace(rows, selected, "b", "b2", onReplaced: () => selected.Remove("b"));

        Assert.Equal(new[] { "a", "b2", "c" }, rows);
        Assert.Equal(new object[] { "b2" }, selected);
    }

    [Fact]
    public void AnUnselectedRowIsReplacedWithoutTouchingTheSelection()
    {
        var rows = new List<string> { "a", "b" };
        IList selected = new ArrayList { "a" };

        RowReplace.Replace(rows, selected, "b", "b2", onReplaced: () => { });

        Assert.Equal(new[] { "a", "b2" }, rows);
        Assert.Equal(new object[] { "a" }, selected);
    }
}
