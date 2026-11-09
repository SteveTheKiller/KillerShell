using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Shell;

// A favorite on a share that is offline, or a drive that is unplugged, at launch is not shown,
// but it stays saved: the next save must not delete it for good.
public sealed class BookmarkPersistenceTests
{
    private const string Local = @"C:\a";
    private const string Share = @"\\nas\share";
    private const string ThisPc = ":ThisPC:";

    [Fact]
    public void AnUnreachableFavoriteIsHiddenButSurvivesTheNextSave()
    {
        var loaded = SavedBookmarks.Parse(Local + "|" + Share + "|" + ThisPc, path => path != Share);

        Assert.Equal(new[] { Local, ThisPc }, loaded.Visible);

        // The Recycle Bin migration inserts a row and saves straight away.
        var shown = new System.Collections.Generic.List<string>(loaded.Visible);
        shown.Insert(2, ":RecycleBin:");
        Assert.Equal(Local + "|" + ThisPc + "|" + ":RecycleBin:" + "|" + Share, SavedBookmarks.Serialize(shown, loaded.Held));
    }

    [Fact]
    public void AFavoriteReAddedWhileHeldIsSavedOnce()
    {
        var loaded = SavedBookmarks.Parse(Share + "|" + Local, path => path != Share);
        Assert.Equal(Local + "|" + @"\\NAS\share", SavedBookmarks.Serialize(new[] { Local, @"\\NAS\share" }, loaded.Held));
    }

    [Fact]
    public void AnEmptySettingStaysEmpty()
        => Assert.Equal("", SavedBookmarks.Serialize(SavedBookmarks.Parse("", _ => true).Visible, SavedBookmarks.Parse("", _ => true).Held));
}
