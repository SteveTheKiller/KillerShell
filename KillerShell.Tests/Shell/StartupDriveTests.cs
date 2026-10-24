using System;
using System.Linq;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

// A drive double-click arrives as "E:\." and becomes E:\ (StartupPaths.cs). A drive that is not
// ready, or a network place that needs reconnecting or signing in to, goes to Explorer for its
// prompt; a missing folder on a drive that is there is still skipped quietly.
public sealed class StartupDriveTests
{
    private static Func<string, bool> Present(params string[] folders)
        => path => folders.Contains(path, StringComparer.OrdinalIgnoreCase);

    // A window Explorer opened for a folder is a task window: closing it must not replace the
    // remembered session, ask to quit, hide to the tray or check for updates. Ctrl+N (--cwd) and
    // torn-out documents (--edit) stay ordinary windows.
    [Theory]
    [InlineData(new[] { "--new-window", @"C:\Users\." }, 1, true)]
    [InlineData(new[] { "--new-window", "--cwd", @"C:\Users" }, 0, false)]
    [InlineData(new[] { "--new-window", "--edit", @"C:\notes.txt" }, 1, false)]
    [InlineData(new[] { @"C:\Users\." }, 1, false)]
    [InlineData(new[] { "--new-window" }, 0, false)]
    public void OnlyAFolderLaunchIsAFolderWindow(string[] args, int startupPaths, bool folderWindow)
        => Assert.Equal(folderWindow, MainWindow.IsFolderLaunch(args, startupPaths));

    [Theory]
    [InlineData(@"E:\", true)]                        // no disc, a card reader with no card
    [InlineData(@"Z:\Projects", true)]                // a mapped drive to reconnect
    [InlineData(@"\\server\share\folder", true)]      // a share to sign in to, or offline
    [InlineData(@"\\server\share\", true)]
    [InlineData(@"C:\Gone", false)]                   // a stale shortcut: skipped quietly
    [InlineData(@"C:\Users", false)]                  // there, so KillerShell opens it
    public void PlacesExplorerCanRecoverGoToExplorer(string path, bool explorer)
        => Assert.Equal(explorer, MainWindow.NeedsExplorer(path, Present(@"C:\", @"C:\Users")));

    [Theory]
    [InlineData(@"E:\", true)]
    [InlineData(@"c:\", true)]
    [InlineData(@"E:\Photos", false)]
    [InlineData(@"\\server\share\", false)]
    [InlineData(@"\\server\share\folder", false)]
    public void OnlyADriveRootCountsAsADrive(string path, bool drive)
        => Assert.Equal(drive, MainWindow.IsDriveRoot(path));
}
