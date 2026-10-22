using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ReleaseFileOperationTests
{
    [Fact]
    public void CancelBeforeTheFinalLinkMoveKeepsTheLinkAndReportsCancellation()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        string loose = Path.Combine(fixture.Root, "loose.txt");
        File.WriteAllText(loose, "first");
        string link = fixture.CreateLink();
        var result = FileOps.CopyOrMove(new[] { loose, link }, fixture.Output, true,
            _ => ConflictChoice.Replace, progress =>
            {
                if (progress.CurrentFile == link) cancel.Cancel();
            }, cancel.Token);

        Assert.True(result.Canceled);
        Assert.Equal(1, result.Succeeded);
        Assert.Empty(result.Failed);
        Assert.True(Directory.Exists(link));
        Assert.False(Directory.Exists(Path.Combine(fixture.Output, "link")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(fixture.Output, "loose.txt")));
        Assert.Equal("sentinel", File.ReadAllText(Path.Combine(fixture.Target, "keep.txt")));
    }

    [Fact]
    public void SkippingARootLinkDoesNotPruneItsTarget()
    {
        using var fixture = new Fixture();
        string loose = Path.Combine(fixture.Root, "loose.txt");
        File.WriteAllText(loose, "first");
        string empty = Directory.CreateDirectory(Path.Combine(fixture.Target, "empty")).FullName;
        string link = fixture.CreateLink();
        Directory.CreateDirectory(Path.Combine(fixture.Output, "link"));
        var result = FileOps.CopyOrMove(new[] { link, loose }, fixture.Output, true,
            _ => ConflictChoice.Skip, _ => { }, CancellationToken.None);

        Assert.Empty(result.Failed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Skipped);
        Assert.True(Directory.Exists(link));
        Assert.True(Directory.Exists(empty), "Cleanup traversed a skipped junction.");
        Assert.Equal("sentinel", File.ReadAllText(Path.Combine(fixture.Target, "keep.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyIncomingMovePreservesAttributesAndBothPayloadsOnFailure(bool fail)
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "sample.txt");
        string destination = Path.Combine(fixture.Output, "sample.txt");
        File.WriteAllText(source, "incoming");
        File.WriteAllText(destination, "existing");
        File.SetAttributes(source, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var result = fail
            ? FileOps.CopyOrMove(new[] { source }, fixture.Output, true,
                _ => ConflictChoice.Replace, _ => { }, CancellationToken.None,
                (_, _, _) => throw new IOException("PUBLICATION_REFUSED"))
            : FileOps.CopyOrMove(new[] { source }, fixture.Output, true,
                _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);

        if (fail)
        {
            Assert.Equal(0, result.Succeeded);
            Assert.Single(result.Failed);
            Assert.Equal("incoming", File.ReadAllText(source));
            Assert.Equal("existing", File.ReadAllText(destination));
        }
        else
        {
            Assert.Empty(result.Failed);
            Assert.Equal(1, result.Succeeded);
            Assert.False(File.Exists(source));
            Assert.Equal("incoming", File.ReadAllText(destination));
        }
        var attributes = File.GetAttributes(fail ? source : destination);
        Assert.True((attributes & FileAttributes.ReadOnly) != 0);
        Assert.True((attributes & FileAttributes.Hidden) != 0);
        Assert.Empty(Directory.GetFiles(fixture.Output, "*.KillerShell-backup-*"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ks-release-files-" + Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Root, "output");
        internal string Target => Path.Combine(Root, "target");
        internal Fixture()
        {
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Target);
            File.WriteAllText(Path.Combine(Target, "keep.txt"), "sentinel");
        }
        internal string CreateLink()
        {
            string link = Path.Combine(Root, "link");
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + Target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            Assert.True(process.WaitForExit(5000), "Owned junction creation timed out.");
            Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
            return link;
        }
        public void Dispose()
        {
            string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            foreach (string link in new[] { Path.Combine(Root, "link"), Path.Combine(Output, "link") })
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link);
            foreach (string file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
