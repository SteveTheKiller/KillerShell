using System.Collections.Generic;
using System.Linq;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Shell;

// A favorite on a share that is offline, or a drive that is unplugged, at launch stays in the
// drawer where it was, and the next save keeps it. Loading the setting never touches the disk:
// it runs while the window is being built, and a dead share there would hold the window back for
// the whole network timeout on every launch. The drawer's off-thread check (Live) dims it.
public sealed class BookmarkPersistenceTests
{
    private const string Local = @"C:\a";
    private const string Share = @"\\nas\share";
    private const string ThisPc = ":ThisPC:";

    [Fact]
    public void EveryFavoriteLoadsInItsPlaceWithoutAnyCheck()
    {
        // Paths takes only the setting: there is nothing it could check a share with.
        IReadOnlyList<string> paths = SavedBookmarks.Paths(Local + "|" + Share + "|" + ThisPc);

        Assert.Equal(new[] { Local, Share, ThisPc }, paths);
    }

    [Fact]
    public void TheNextSaveKeepsTheUnreachableFavorite()
    {
        var rows = new List<string>(SavedBookmarks.Paths(Local + "|" + Share + "|" + ThisPc));
        // The Recycle Bin migration inserts a row after This PC and saves straight away.
        rows.Insert(3, ":RecycleBin:");
        Assert.Equal(Local + "|" + Share + "|" + ThisPc + "|:RecycleBin:", SavedBookmarks.Serialize(rows));
    }

    [Fact]
    public void AnEmptySettingStaysEmpty()
    {
        var paths = SavedBookmarks.Paths("");
        Assert.Empty(paths);
        Assert.Equal("", SavedBookmarks.Serialize(paths));
    }

    // A language switch rewrites a row's words in place: the dimmed row's "not reachable"
    // tooltip and the place names (This PC, the Recycle Bin) follow it without a restart.
    [Fact]
    public void ALanguageSwitchRefreshesEveryWordOnTheRow()
    {
        var row = new KillerShell.Shell.Bookmark { Path = Share };
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.NotifyLocalized();

        Assert.Contains("Tip", raised);
        Assert.Contains("Name", raised);
        Assert.Contains("NameLabel", raised);
    }

    // A WSL favorite is answered by its own distribution and never reaches the disk check, so a
    // slow disk probe of the same folder started elsewhere (the recents menu) cannot dim it.
    [Fact]
    public void AWslFavoriteIsLiveWithoutTheDiskCheck()
    {
        const string Wsl = @"\\wsl.localhost\Ubuntu\home";
        var asked = new List<string>();
        var live = SavedBookmarks.Live(new[] { Local, Wsl, Share }, paths =>
        {
            asked.AddRange(paths);
            return paths.Where(p => p != Share).ToList();
        });

        Assert.Equal(new[] { Local, Share }, asked);
        Assert.Equal(new[] { Local, Wsl }, live);
    }
}
