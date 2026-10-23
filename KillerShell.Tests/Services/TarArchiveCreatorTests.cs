using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using KillerShell.Services;
using KillerShell.Tests.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TarArchiveCreatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewArchivesPreserveContentLongNamesAndEmptyFoldersWithoutFollowingLinks(bool gzip)
    {
        using var fixture = new FileArchiveLinkFixture();
        string leaf = "日本語" + new string('x', 105) + ".txt";
        File.WriteAllText(Path.Combine(fixture.Source, leaf), "Unicode sample");
        Directory.CreateDirectory(Path.Combine(fixture.Source, "empty"));
        string destination = Path.Combine(fixture.Root, gzip ? "new.tar.gz" : "new.tar");
        var result = TarArchiveCreator.Create(destination, new[] { fixture.Source }, gzip, null, CancellationToken.None);
        Assert.True(result.Ok, result.ErrorDetail);
        Assert.Equal(1, result.SkippedDirectoryLinks);
        var rows = ArchiveProvider.List(destination, "source", out string? error);
        Assert.Null(error);
        Assert.Contains(rows, row => row.Name == "empty" && row.IsDirectory);
        Assert.Contains(rows, row => row.Name == leaf && !row.IsDirectory);
        Assert.DoesNotContain(rows, row => row.Name == "ancestor");
        string extracted = Path.Combine(fixture.Target, "extracted");
        Assert.True(ArchiveProvider.ExtractAll(destination, extracted, CancellationToken.None, out error), error);
        Assert.Equal("Unicode sample", File.ReadAllText(Path.Combine(extracted, "source", leaf)));
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(extracted, "source", "fixture.txt")));
        Assert.True(Directory.Exists(Path.Combine(extracted, "source", "empty")));
        Assert.False(ArchiveWriter.CanWrite(destination));

        // Optional independent UTF-8 validation. Windows bsdtar 3.8.9 rejects names outside CP1252.
        string? python = Environment.GetEnvironmentVariable("KILLERSHELL_ARCHIVE_TEST_PYTHON");
        if (!string.IsNullOrEmpty(python))
        {
            string script = "import sys,tarfile; t=tarfile.open(sys.argv[1]); "
                + "assert t.extractfile('source/'+sys.argv[2]).read()==b'Unicode sample'; "
                + "assert t.extractfile('source/fixture.txt').read()==b'fixture'; "
                + "assert t.getmember('source/empty').isdir(); "
                + "assert len(t.getmembers())==4";
            RunReader(python!, "-c \"" + script + "\" \"" + destination + "\" \"" + leaf + "\"");
        }

        string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (File.Exists(tar))
        {
            string portable = Path.Combine(fixture.Root, gzip ? "portable.tar.gz" : "portable.tar");
            result = TarArchiveCreator.Create(portable, new[] { fixture.File }, gzip, null, CancellationToken.None);
            Assert.True(result.Ok, result.ErrorDetail);
            Assert.Contains("fixture.txt", RunReader(tar, "-tf \"" + portable + "\""));
        }
    }

    [Fact]
    public void SourceInsideAJunctionIsNotFollowed()
    {
        using var fixture = new FileArchiveLinkFixture();
        string destination = Path.Combine(fixture.Root, "linked.tar");
        var result = TarArchiveCreator.Create(destination, new[] { Path.Combine(fixture.Link, "fixture.txt") }, false, null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(1, result.SkippedDirectoryLinks);
        Assert.False(File.Exists(destination));
        string linkedDestination = Path.Combine(fixture.Link, "output.tar");
        Assert.False(TarArchiveCreator.Create(linkedDestination, new[] { fixture.File }, false, null, CancellationToken.None).Ok);
        Assert.False(File.Exists(Path.Combine(fixture.Source, "output.tar")));
    }

    [Fact]
    public void ExistingDestinationAndCancellationLeaveNoPartialArchive()
    {
        using var fixture = new FileArchiveLinkFixture();
        string existing = Path.Combine(fixture.Root, "existing.tar");
        File.WriteAllText(existing, "untouched");
        Assert.False(TarArchiveCreator.Create(existing, new[] { fixture.Source }, false, null, CancellationToken.None).Ok);
        Assert.Equal("untouched", File.ReadAllText(existing));
        string canceled = Path.Combine(fixture.Root, "canceled.tar.gz");
        var result = TarArchiveCreator.Create(canceled, new[] { fixture.Source }, true, null, new CancellationToken(true));
        Assert.False(result.Ok);
        Assert.Equal("Str_Status_ArchiveCanceled", result.ErrorKey);
        Assert.False(File.Exists(canceled));
        using var cancellation = new CancellationTokenSource();
        result = TarArchiveCreator.Create(canceled, new[] { fixture.Source }, true,
            (_, _, _) => cancellation.Cancel(), cancellation.Token);
        Assert.False(result.Ok);
        Assert.Equal("Str_Status_ArchiveCanceled", result.ErrorKey);
        Assert.False(File.Exists(canceled));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.kscreate-*"));
    }

    [Fact]
    public void DestinationCreatedDuringCompressionIsNeverReplaced()
    {
        using var fixture = new FileArchiveLinkFixture();
        string destination = Path.Combine(fixture.Root, "race.tar");
        var result = TarArchiveCreator.Create(destination, new[] { fixture.Source }, false,
            (_, _, _) => { if (!File.Exists(destination)) File.WriteAllText(destination, "another writer"); }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("another writer", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.kscreate-*"));
    }

    private static string RunReader(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(5000);
        if (!exited) process.Kill();
        Assert.True(exited, "The independent archive reader exceeded its deadline.");
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }
}
