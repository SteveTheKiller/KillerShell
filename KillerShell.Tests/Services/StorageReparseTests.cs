using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using KillerShell.Services;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Services;

internal sealed class ReparseFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ks-reparse-" + Guid.NewGuid().ToString("N"));
    internal string Scan => Path.Combine(Root, "scan");
    internal string Outside => Path.Combine(Root, "outside");

    internal ReparseFixture()
    {
        Directory.CreateDirectory(Scan);
        Directory.CreateDirectory(Outside);
        File.WriteAllBytes(Path.Combine(Scan, "real.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(Outside, "elsewhere.bin"), new byte[7000]);
    }

    internal void AddJunction(string name) => MakeLink("/J", Path.Combine(Scan, name), Outside);
    internal void AddDirectorySymlink(string name) => MakeLink("/D", Path.Combine(Scan, name), Outside);
    internal void AddFileSymlink(string name, string target) => MakeLink("", Path.Combine(Scan, name), target);

    private static void MakeLink(string kind, string link, string target)
    {
        string arguments = "/c mklink " + kind + " \"" + link + "\" \"" + target + "\"";
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000), "Link creation timed out.");
        Assert.True(process.ExitCode == 0, text);
    }

    public void Dispose()
    {
        string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        // A directory link is removed as a link, so its target is never walked or deleted.
        foreach (string entry in Directory.GetDirectories(Scan))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry);
        Directory.Delete(Root, true);
    }
}

public sealed class StorageReparseTests
{
    [Fact]
    public void DirectoryLinksAreSkippedAndCounted()
    {
        using var fixture = new ReparseFixture();
        fixture.AddJunction("junction");
        fixture.AddDirectorySymlink("folderlink");

        var read = StorageDirectoryReader.Read(fixture.Scan, CancellationToken.None);

        Assert.Equal(["real.bin"], read.Entries.Select(entry => entry.Name).ToArray());
        Assert.Equal(2, read.SkippedReparsePoints);
    }

    [Fact]
    public void ALinkedFileIsKeptWithTheSizeTheDirectoryReports()
    {
        using var fixture = new ReparseFixture();
        fixture.AddFileSymlink("filelink.bin", Path.Combine(fixture.Outside, "elsewhere.bin"));

        var read = StorageDirectoryReader.Read(fixture.Scan, CancellationToken.None);

        var link = Assert.Single(read.Entries, entry => entry.Name == "filelink.bin");
        Assert.True(link.IsReparsePoint);
        Assert.False(link.IsDirectory);
        // The link's own size, never the 7000 bytes of what it points at.
        Assert.Equal(new FileInfo(Path.Combine(fixture.Scan, "filelink.bin")).Length, link.Size);
        Assert.Equal(0, read.SkippedReparsePoints);
    }

    [Fact]
    public void TheMapReadStillLeavesLinkedFilesOut()
    {
        using var fixture = new ReparseFixture();
        fixture.AddJunction("junction");
        fixture.AddFileSymlink("filelink.bin", Path.Combine(fixture.Outside, "elsewhere.bin"));

        var read = StorageDirectoryReader.ReadForMap(fixture.Scan, CancellationToken.None);

        // The treemap has always left every reparse entry out; its totals do not move.
        Assert.Equal(["real.bin"], read.Entries.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void ScanCountsLinkedFilesAndDirectoryLinks()
    {
        using var fixture = new ReparseFixture();
        fixture.AddJunction("junction");
        fixture.AddDirectorySymlink("folderlink");
        fixture.AddFileSymlink("filelink.bin", Path.Combine(fixture.Outside, "elsewhere.bin"));

        var scan = StorageScan.Scan(fixture.Scan, 10, false, CancellationToken.None);

        Assert.Equal(2, scan.Files);
        Assert.Equal(100, scan.TotalBytes);
        Assert.Equal(2, scan.SkippedReparsePoints);
        Assert.False(scan.RootUnreadable);
    }

    [Fact]
    public void ACancelledTokenMarksTheScanTruncatedEvenWithNothingToWalk()
    {
        string empty = Path.Combine(Path.GetTempPath(), "ks-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            using var source = new CancellationTokenSource();
            source.Cancel();

            var scan = StorageScan.Scan(empty, 10, false, source.Token);

            Assert.True(scan.Truncated);
            Assert.Empty(scan.TopFiles);
        }
        finally { Directory.Delete(empty); }
    }

    [Fact]
    public void ACompleteScanIsNotTruncated()
    {
        using var fixture = new ReparseFixture();

        var scan = StorageScan.Scan(fixture.Scan, 10, false, CancellationToken.None);

        Assert.False(scan.Truncated);
    }

    [Fact]
    public void AMissingRootIsUnreadable()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ks-missing-" + Guid.NewGuid().ToString("N"));

        var scan = StorageScan.Scan(missing, 10, false, CancellationToken.None);

        Assert.True(scan.RootUnreadable);
    }

    [Fact]
    public void ATreeScanReportsItsPruningThreshold()
    {
        using var fixture = new ReparseFixture();
        File.WriteAllBytes(Path.Combine(fixture.Scan, "large.bin"), new byte[100_000]);

        var scan = StorageScan.Scan(fixture.Scan, 10, true, CancellationToken.None);

        Assert.Equal(100_100 / 5000, scan.PruneThresholdBytes);

        var plain = StorageScan.Scan(fixture.Scan, 10, false, CancellationToken.None);
        Assert.Equal(0, plain.PruneThresholdBytes);
    }

    [Fact]
    public void AnIncompleteReportSaysSoAndACompleteOneStaysQuiet()
    {
        string output = Path.Combine(Path.GetTempPath(), "KillerShell.Tests-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            var root = new StorageReportNode("root", "C:\\data", 10, true);
            StorageHtmlExporter.Export(output, new StorageReport("C:\\data", "C:\\data", 10, 0, 0, false, root));
            Assert.DoesNotContain("Partial scan", File.ReadAllText(output));
            File.Delete(output);

            StorageHtmlExporter.Export(output, new StorageReport("C:\\data", "C:\\data", 10, 0, 2, false, root,
                truncated: true, skippedDirectories: 3));
            string html = File.ReadAllText(output);
            Assert.Contains("Partial scan", html);
            Assert.Contains("Folders not read: 3", html);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
