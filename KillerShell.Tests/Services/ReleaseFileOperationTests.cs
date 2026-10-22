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
