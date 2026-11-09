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

    // Only the main window keeps the remembered session. A Ctrl+N window and a torn-out tab start
    // without restoring it, so closing one must not overwrite the main window's tabs either, the
    // same as an admin window or a task window.
    [Theory]
    [InlineData(new string[0], false, false, false)]
    [InlineData(new[] { @"C:\Users" }, false, false, false)]
    [InlineData(new[] { "--new-window", "--cwd", @"C:\Users" }, false, false, true)]
    [InlineData(new[] { "--new-window", "--edit", @"C:\notes.txt" }, false, false, true)]
    [InlineData(new[] { "--new-window" }, false, false, true)]
    [InlineData(new string[0], true, false, true)]
    [InlineData(new string[0], false, true, true)]
    public void OnlyTheMainWindowKeepsTheSession(string[] args, bool elevated, bool taskWindow, bool keepsNone)
        => Assert.Equal(keepsNone, MainWindow.KeepsNoSession(args, elevated, taskWindow));

    // Esc in a window: a remembered "stay" holds in a Ctrl+N or torn-out window too. Those
    // windows keep no session, so with nothing remembered they close without the keep-tabs
    // prompt. An admin or task window closes as it always has.
    [Theory]
    [InlineData(false, false, "", MainWindow.EscAction.Prompt)]
    [InlineData(false, false, "stay", MainWindow.EscAction.Stay)]
    [InlineData(false, false, "quit", MainWindow.EscAction.Close)]
    [InlineData(false, true, "stay", MainWindow.EscAction.Stay)]
    [InlineData(false, true, "", MainWindow.EscAction.Close)]
    [InlineData(false, true, "quit", MainWindow.EscAction.Close)]
    [InlineData(true, true, "stay", MainWindow.EscAction.Close)]
    [InlineData(true, true, "", MainWindow.EscAction.Close)]
    public void EscHonorsTheRememberedChoice(bool elevatedOrTask, bool keepsNoSession, string remembered, MainWindow.EscAction expected)
        => Assert.Equal(expected, MainWindow.DecideEsc(elevatedOrTask, keepsNoSession, remembered));

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
