using System;
using System.Diagnostics;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class DirectoryLinkTests
{
    [Theory]
    [InlineData(DirectoryLink.TagMountPoint, true)]   // junction
    [InlineData(DirectoryLink.TagSymlink, true)]      // symbolic link
    [InlineData(0x80000013u, false)]                  // IO_REPARSE_TAG_DEDUP
    [InlineData(0x80000017u, false)]                  // IO_REPARSE_TAG_WOF
    [InlineData(0x9000001Cu, false)]                  // IO_REPARSE_TAG_PROJFS
    [InlineData(0x9000601Au, false)]                  // IO_REPARSE_TAG_CLOUD_6 (OneDrive placeholder)
    [InlineData(0x8000001Bu, false)]                  // IO_REPARSE_TAG_APPEXECLINK has no surrogate bit
    public void OnlyNameSurrogateTagsAreLinks(uint tag, bool link)
        => Assert.Equal(link, DirectoryLink.IsNameSurrogate(tag));

    [Fact]
    public void AJunctionIsALinkAndAPlainFolderIsNot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-dirlink-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "target");
        string junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        try
        {
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + target + "\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
            {
                Assert.True(process.WaitForExit(5000), "Junction creation timed out.");
                Assert.Equal(0, process.ExitCode);
            }
            Assert.True(DirectoryLink.IsLink(junction));
            Assert.Equal(DirectoryLink.TagMountPoint, DirectoryLink.ReadTag(junction));
            Assert.True(DirectoryLink.IsLink(junction + "\\"), "A trailing separator must not hide the link.");
            Assert.False(DirectoryLink.IsLink(target));
            Assert.False(DirectoryLink.IsLink(Path.Combine(root, "missing")));
            Assert.False(DirectoryLink.IsLink(Path.GetPathRoot(root)!));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, true);
        }
    }
}
