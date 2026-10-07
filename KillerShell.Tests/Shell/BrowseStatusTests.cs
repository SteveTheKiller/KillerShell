using System;
using System.IO;
using System.Threading;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class BrowseStatusTests
{
    [Theory]
    [InlineData("Str_Status_Listing", false, "WarnBrush")]
    [InlineData("Str_Status_Listed", false, "OkBrush")]
    [InlineData("Str_Status_BadPath", false, "DangerRed")]
    [InlineData("Str_Status_BadPath", true, "DangerRed")]
    [InlineData("Str_Status_ArchiveFailed", true, "DangerRed")]
    [InlineData(null, true, "WarnBrush")]
    [InlineData(null, false, "OkBrush")]
    public void FolderStatusDistinguishesLoadingSuccessAndFailure(string? key, bool searching, string expected)
        => Assert.Equal(expected, MainWindow.StatusBrushKey(key, searching));

    [Fact]
    public void UnreadableLocationIsNotReportedAsAnEmptyFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Empty(MainWindow.ListFolder(root, CancellationToken.None, out bool emptyFailed));
            Assert.False(emptyFailed);
            Assert.Empty(MainWindow.ListFolder(Path.Combine(root, "missing"), CancellationToken.None, out bool missingFailed));
            Assert.True(missingFailed);
            string file = Path.Combine(root, "file.txt");
            File.WriteAllText(file, "fixture");
            Assert.Empty(MainWindow.ListFolder(file, CancellationToken.None, out bool fileFailed));
            Assert.True(fileFailed);
        }
        finally
        {
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The fixture path escaped the temporary directory.");
            Directory.Delete(root, true);
        }
    }
}
