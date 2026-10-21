using System;
using System.IO;
using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossDeviceRenameToAbsentDestinationUsesStagedCopy(bool keepBoth)
    {
        if (!keepBoth) File.Delete(Destination);
        string chosen = keepBoth ? Path.Combine(Target, "file (2).txt") : Destination;
        int renames = 0;
        var result = FileOps.CopyOrMove(new[] { Source }, Target, true, _ => ConflictChoice.KeepBoth,
            _ => { }, CancellationToken.None, File.Replace, renameFile: (incoming, destination, flags) =>
            {
                Assert.Equal(0u, flags);
                renames++;
                Assert.Equal(chosen, destination);
                if (incoming == Source) throw CrossDevice();
                Assert.Equal(Target, Path.GetDirectoryName(incoming));
                File.Move(incoming, destination);
            });

        Assert.Empty(result.Failed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(chosen, Assert.Single(result.SucceededTargets));
        Assert.Equal(2, renames);
        Assert.False(File.Exists(Source));
        Assert.Equal("incoming bytes", File.ReadAllText(chosen));
        if (keepBoth) Assert.Equal("previous destination", File.ReadAllText(Destination));
        Assert.Equal(keepBoth ? 2 : 1, Directory.GetFiles(Target).Length);
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
        int fileRenames = 0;
        var result = FileOps.CopyOrMove(new[] { source }, Target, true,
            _ => { conflicts++; return ConflictChoice.KeepBoth; }, _ => { }, CancellationToken.None,
            File.Replace, (from, to) =>
            {
                directoryMoves++;
                Assert.Equal(source, from);
                Assert.Equal(chosen, to);
                throw CrossDevice();
            }, (from, to, flags) =>
            {
                Assert.Equal(0u, flags);
                fileRenames++;
                Assert.Equal(Path.Combine(chosen, "file.txt"), to);
                if (from == Source) throw CrossDevice();
                Assert.Equal(chosen, Path.GetDirectoryName(from));
                File.Move(from, to);
            });

        Assert.Empty(result.Failed);
        Assert.Equal(1, directoryMoves);
        Assert.Equal(2, fileRenames);
        Assert.Equal(Path.Combine(chosen, "file.txt"), Assert.Single(result.SucceededTargets));
        Assert.Equal(keepBoth ? 1 : 0, conflicts);
        Assert.False(Directory.Exists(source));
        Assert.Equal("incoming bytes", File.ReadAllText(Path.Combine(chosen, "file.txt")));
        Assert.Equal("previous destination", File.ReadAllText(Destination));
        if (keepBoth) Assert.Equal("existing folder", File.ReadAllText(Path.Combine(existing, "keep.txt")));
    }

    [Fact]
    public void SuccessfulNativeRenameToAbsentDestinationDoesNotCopy()
    {
        File.Delete(Destination);
        using var sourceLock = new FileStream(Source, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        bool copiedBytes = false;
        var result = FileOps.CopyOrMove(new[] { Source }, Target, true, _ => ConflictChoice.Replace,
            progress => copiedBytes |= progress.BytesDone > 0 && progress.ItemsDone == 0, CancellationToken.None);

        Assert.Empty(result.Failed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(Destination, Assert.Single(result.SucceededTargets));
        Assert.False(copiedBytes);
        Assert.False(File.Exists(Source));
        Assert.Equal("incoming bytes", File.ReadAllText(Destination));
        Assert.Equal(new[] { Destination }, Directory.GetFiles(Target));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CrossDeviceNewDestinationCancellationPreservesSource(bool directory, bool afterPublication)
    {
        if (!directory) File.Delete(Destination);
        string chosenDirectory = directory ? Path.Combine(Target, "source") : Target;
        string chosen = Path.Combine(chosenDirectory, "file.txt");
        var bytes = new byte[12 << 20];
        bytes[0] = 3;
        bytes[bytes.Length - 1] = 9;
        File.WriteAllBytes(Source, bytes);
        using var cancel = new CancellationTokenSource();
        bool interruptedCopy = false;
        bool published = false;
        var result = FileOps.CopyOrMove(new[] { directory ? Path.GetDirectoryName(Source)! : Source },
            Target, true, _ => ConflictChoice.Replace, progress =>
            {
                if (!afterPublication && progress.BytesDone > 0 && progress.BytesDone < progress.BytesTotal)
                {
                    interruptedCopy = true;
                    cancel.Cancel();
                }
            }, cancel.Token, File.Replace, (_, _) => throw CrossDevice(), (incoming, destination, flags) =>
            {
                Assert.Equal(0u, flags);
                Assert.Equal(chosen, destination);
                if (incoming == Source) throw CrossDevice();
                Assert.Equal(chosenDirectory, Path.GetDirectoryName(incoming));
                File.Move(incoming, destination);
                published = true;
                if (afterPublication) cancel.Cancel();
            });

        Assert.True(result.Canceled);
        Assert.Equal(0, result.Succeeded);
        Assert.Empty(result.SucceededTargets);
        Assert.Empty(result.Failed);
        Assert.Equal(!afterPublication, interruptedCopy);
        Assert.Equal(afterPublication, published);
        Assert.Equal(bytes, File.ReadAllBytes(Source));
        if (afterPublication) Assert.Equal(bytes, File.ReadAllBytes(chosen));
        else Assert.False(File.Exists(chosen));
        Assert.Equal(afterPublication ? 1 : 0, Directory.GetFiles(chosenDirectory).Length);
        if (directory) Assert.Equal("previous destination", File.ReadAllText(Destination));
    }

    [Fact]
    public void NativeRenameMovesTheFileLinkWithoutChangingItsTarget()
    {
        string link = Path.Combine(Path.GetDirectoryName(Source)!, "file-link.txt");
        string movedLink = Path.Combine(Target, "file-link.txt");
        Assert.True(CreateSymbolicLink(link, Source, 0x2), "The fixture requires unprivileged symbolic links.");
        var result = FileOps.CopyOrMove(new[] { link }, Target, true, _ => ConflictChoice.Replace,
            _ => { }, CancellationToken.None);

        Assert.Empty(result.Failed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(movedLink, Assert.Single(result.SucceededTargets));
        Assert.False(File.Exists(link));
        Assert.True((File.GetAttributes(movedLink) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal("incoming bytes", File.ReadAllText(movedLink));
        Assert.Equal("incoming bytes", File.ReadAllText(Source));
        Assert.Equal("previous destination", File.ReadAllText(Destination));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    public void OtherNativeRenameErrorsDoNotCopyToAnAbsentDestination(int code)
    {
        File.Delete(Destination);
        int renames = 0;
        var result = FileOps.CopyOrMove(new[] { Source }, Target, true, _ => ConflictChoice.Replace,
            _ => { }, CancellationToken.None, File.Replace, renameFile: (_, _, flags) =>
            {
                Assert.Equal(0u, flags);
                renames++;
                throw new IOException("Controlled native refusal", unchecked((int)0x80070000) | code);
            });

        Assert.Single(result.Failed);
        Assert.Equal(0, result.Succeeded);
        Assert.Empty(result.SucceededTargets);
        Assert.Equal(1, renames);
        Assert.Equal("incoming bytes", File.ReadAllText(Source));
        Assert.Empty(Directory.GetFiles(Target));
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLink(string link, string target, int flags);

    public void Dispose() => Directory.Delete(_root, true);
}
