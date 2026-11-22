using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

// The runtime scenario runner resolves scenarios in this namespace (TerminalOutputFlowTests.cs).
namespace KillerShell.Tests.Terminal;

/// <summary>
/// A This PC tab restored from the last session was never listed: the tab switch only refreshed
/// a tab whose folder exists on disk, and This PC is a sentinel with no directory behind it, so
/// the tab opened empty and its strip icon fell back to nothing.
/// </summary>
[Trait("Category", "Window")]
public sealed class ThisPcTabSwitchTests
{
    [Fact]
    public Task SwitchingToARestoredThisPcTabListsTheDrives()
        => RuntimeComponentProcess.Run(nameof(ThisPcTabSwitchScenario));
}

public static class ThisPcTabSwitchScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active != null && right.Active.IsBrowsing);
        window.FocusPane(left);

        // The shape session restore gives a This PC tab: browsing the sentinel, with no rows yet.
        var restored = new SearchTab("This PC") { CurrentFolder = MainWindow.ThisPc, IsBrowsing = true };
        Assert.Empty(restored.Results);
        left.Tabs.Add(restored);
        left.Active = restored;
        window.FocusPane(right);
        window.FocusPane(left);

        int drives = Browse_DriveCount();
        Assert.True(drives > 0, "This machine reports no drives, so the scenario cannot tell a listed tab from an empty one.");
        ShellWindowFixture.PumpUntil(() => restored.Results.Count == drives,
            diagnostic: () => "rows=" + restored.Results.Count + " drives=" + drives + " status=" + restored.StatusKey);
        Assert.All(restored.Results, r => Assert.EndsWith(@":\", r.FilePath));

        var icon = new TabFolderIconConverter().Convert(restored, typeof(ImageSource), null!, CultureInfo.InvariantCulture);
        Assert.NotNull(icon);
        Assert.Same(IconCache.Art("my_pc_icon"), icon);
    }

    private static int Browse_DriveCount() => MainWindow.ListDrives().Count;
}
