using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

// A drive double-click arrives as "E:\." and becomes E:\ (StartupPaths.cs). A drive that is not
// ready goes to Explorer for its prompt; a missing folder is still skipped quietly.
public sealed class StartupDriveTests
{
    [Theory]
    [InlineData(@"E:\", true)]
    [InlineData(@"c:\", true)]
    [InlineData(@"E:\Photos", false)]
    [InlineData(@"\\server\share\", false)]
    [InlineData(@"\\server\share\folder", false)]
    public void OnlyADriveRootCountsAsADrive(string path, bool drive)
        => Assert.Equal(drive, MainWindow.IsDriveRoot(path));
}
