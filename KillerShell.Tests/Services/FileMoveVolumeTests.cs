using System;
using System.IO;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class FileMoveVolumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ks-mounted-move-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source", "file.txt");
    private string Destination => Path.Combine(Target, "file.txt");
    private string Target => Path.Combine(_root, "mounted-volume");

    public FileMoveVolumeTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        Directory.CreateDirectory(Target);
        File.WriteAllText(Source, "incoming bytes");
        File.WriteAllText(Destination, "previous destination");
    }

    [Fact]
    public void CrossDeviceReplacementFallsBackToCopyAndDeleteWithinTheChosenTarget()
    {
        int replacements = 0;
        var result = FileOps.CopyOrMove(new[] { Source }, Target, true, _ => ConflictChoice.Replace,
            _ => { }, CancellationToken.None, (incoming, destination, backup) =>
            {
                replacements++;
                Assert.Equal(Destination, destination);
                if (incoming == Source) throw CrossDevice();
                Assert.Equal(Target, Path.GetDirectoryName(incoming));
                File.Replace(incoming, destination, backup);
            });

        Assert.Empty(result.Failed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(Destination, Assert.Single(result.SucceededTargets));
        Assert.Equal(2, replacements);
        Assert.False(File.Exists(Source));
        Assert.Equal("incoming bytes", File.ReadAllText(Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossDeviceDirectoryMovePreservesTheAlreadyChosenTarget(bool keepBoth)
    {
        string source = Path.GetDirectoryName(Source)!;
        string existing = Path.Combine(Target, "source");
        if (keepBoth)
        {
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.txt"), "existing folder");
        }
        string chosen = Path.Combine(Target, keepBoth ? "source (2)" : "source");
        int conflicts = 0;
        int directoryMoves = 0;
        var result = FileOps.CopyOrMove(new[] { source }, Target, true,
            _ => { conflicts++; return ConflictChoice.KeepBoth; }, _ => { }, CancellationToken.None,
            File.Replace, (from, to) =>
            {
                directoryMoves++;
                Assert.Equal(source, from);
                Assert.Equal(chosen, to);
                throw CrossDevice();
            });

        Assert.Empty(result.Failed);
        Assert.Equal(1, directoryMoves);
        Assert.Equal(keepBoth ? 1 : 0, conflicts);
        Assert.False(Directory.Exists(source));
        Assert.Equal("incoming bytes", File.ReadAllText(Path.Combine(chosen, "file.txt")));
        Assert.Equal("previous destination", File.ReadAllText(Destination));
        if (keepBoth) Assert.Equal("existing folder", File.ReadAllText(Path.Combine(existing, "keep.txt")));
    }

    [Fact]
    public void CancellationAfterCopyPublicationKeepsTheSource()
    {
        using var cancel = new CancellationTokenSource();
        var result = FileOps.CopyOrMove(new[] { Source }, Target, true, _ => ConflictChoice.Replace,
            _ => { }, cancel.Token, (incoming, destination, backup) =>
            {
                if (incoming == Source) throw CrossDevice();
                File.Replace(incoming, destination, backup);
                cancel.Cancel();
            });

        Assert.True(result.Canceled);
        Assert.Equal(0, result.Succeeded);
        Assert.Empty(result.Failed);
        Assert.Equal("incoming bytes", File.ReadAllText(Source));
        Assert.Equal("incoming bytes", File.ReadAllText(Destination));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(32, false)]
    [InlineData(5, true)]
    [InlineData(32, true)]
    public void OtherNativeErrorsNeverSelectTheCopyFallback(int code, bool directory)
    {
        int calls = 0;
        void Refuse() { calls++; throw new IOException("Controlled native refusal", unchecked((int)0x80070000) | code); }
        var result = FileOps.CopyOrMove(new[] { directory ? Path.GetDirectoryName(Source)! : Source },
            Target, true, _ => ConflictChoice.Replace, _ => { }, CancellationToken.None,
            (_, _, _) => Refuse(), (_, _) => Refuse());

        Assert.Single(result.Failed);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, calls);
        Assert.Equal("incoming bytes", File.ReadAllText(Source));
        Assert.Equal("previous destination", File.ReadAllText(Destination));
        Assert.False(Directory.Exists(Path.Combine(Target, "source")));
    }

    [Fact]
    public void SourceDeletionFailureReportsBothPreservedCopies()
    {
        using var sourceLock = new FileStream(Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = FileOps.CopyOrMove(new[] { Source }, Target, true, _ => ConflictChoice.Replace,
            _ => { }, CancellationToken.None, (incoming, destination, backup) =>
            {
                if (incoming == Source) throw CrossDevice();
                File.Replace(incoming, destination, backup);
            });

        Assert.Contains("could not remove source", Assert.Single(result.Failed).Error);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal("incoming bytes", File.ReadAllText(Source));
        Assert.Equal("incoming bytes", File.ReadAllText(Destination));
    }

    [Fact]
    public void PreCanceledMoveNeverReachesTheNativeBoundary()
    {
        int calls = 0;
        var result = FileOps.CopyOrMove(new[] { Path.GetDirectoryName(Source)! }, Target, true,
            _ => ConflictChoice.Replace, _ => { }, new CancellationToken(true),
            (_, _, _) => calls++, (_, _) => calls++);
        Assert.True(result.Canceled);
        Assert.Equal(0, calls);
        Assert.Equal("incoming bytes", File.ReadAllText(Source));
        Assert.Equal("previous destination", File.ReadAllText(Destination));
    }

    internal static IOException CrossDevice() => new("Controlled ERROR_NOT_SAME_DEVICE", unchecked((int)0x80070011));

    public void Dispose() => Directory.Delete(_root, true);
}
