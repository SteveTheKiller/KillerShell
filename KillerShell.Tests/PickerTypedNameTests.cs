using KillerShell.Controls;
using Xunit;

namespace KillerShell.Tests;

// The save picker's typed name. A name pasted with its quotes ("report.csv") is a normal name:
// changing the file type swaps its extension instead of crashing, and a quoted full path counts
// as a path the picker can accept without a folder listing.
public sealed class PickerTypedNameTests
{
    private static readonly string[] Exts = [".csv", ".txt"];

    [Theory]
    [InlineData("\"report.csv\"", ".txt", "report.txt")]
    [InlineData("report.csv", ".txt", "report.txt")]
    [InlineData("report.csv", ".csv", null)]
    [InlineData("notes.md", ".txt", null)]          // an extension the user typed by hand stays
    [InlineData("report", ".txt", null)]
    [InlineData("", ".txt", null)]
    [InlineData("report.csv", null, null)]
    public void ChangingTheTypeSwapsOnlyAFilterExtension(string typed, string? newExt, string? expected)
        => Assert.Equal(expected, FileDialog.SwapTypedExtension(typed, newExt, Exts));

    [Theory]
    [InlineData("\"C:\\out\\report.txt\"", true)]
    [InlineData("C:\\out\\report.txt", true)]
    [InlineData("report.txt", false)]
    public void AQuotedFullPathIsAcceptedWithoutAListing(string typed, bool expected)
        => Assert.Equal(expected, PickerAcceptRule.CanAccept(false, false, false, false, typed));
}
