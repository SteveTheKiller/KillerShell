using System.Collections.Generic;
using System.Linq;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Shell;

// A favorite on a share that is offline, or a drive that is unplugged, at launch stays in the
// drawer where it was, marked unreachable so it can be shown dimmed, and the next save keeps it.
public sealed class BookmarkPersistenceTests
{
    private const string Local = @"C:\a";
    private const string Share = @"\\nas\share";
    private const string ThisPc = ":ThisPC:";

    [Fact]
    public void AnUnreachableFavoriteKeepsItsPlaceAndIsMarked()
    {
        var loaded = SavedBookmarks.Parse(Local + "|" + Share + "|" + ThisPc, path => path != Share);

        Assert.Equal(new[] { Local, Share, ThisPc }, loaded.Paths);
        Assert.Equal(new[] { Share }, loaded.Unreachable);
    }

    [Fact]
    public void TheNextSaveKeepsTheUnreachableFavorite()
    {
        var loaded = SavedBookmarks.Parse(Local + "|" + Share + "|" + ThisPc, path => path != Share);
        // The Recycle Bin migration inserts a row after This PC and saves straight away.
        var rows = new List<string>(loaded.Paths);
        rows.Insert(3, ":RecycleBin:");
        Assert.Equal(Local + "|" + Share + "|" + ThisPc + "|:RecycleBin:", SavedBookmarks.Serialize(rows));
    }

    [Fact]
    public void AnEmptySettingStaysEmpty()
    {
        var loaded = SavedBookmarks.Parse("", _ => true);
        Assert.Empty(loaded.Paths);
        Assert.Equal("", SavedBookmarks.Serialize(loaded.Paths));
    }
}
