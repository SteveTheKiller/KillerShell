using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class RecoveryImageOwnershipTests
{
    [Fact]
    public void ValidationExceptionCanBeRetriedAndThenUninstalled()
    {
        using var fixture = new Fixture();
        string candidate = Path.Combine(fixture.Root, "candidate.exe");
        File.WriteAllText(candidate, "replacement image");

        var failure = Assert.Throws<IOException>(() => VerifiedFile.ReplaceImage(fixture.Image,
            Encoding.UTF8.GetBytes("replacement image"), _ => throw new IOException("validation unavailable")));
        Assert.Contains("validation unavailable", failure.Message);
        Assert.Equal("installed image", File.ReadAllText(fixture.Image));

        InstallationImage.Publish(candidate, fixture.Image);
        using var removal = UninstallRemoval.Begin(false, fixture.Environment);
        removal.Commit();

        Assert.False(Directory.Exists(fixture.Directory));
        Assert.Equal("replacement image", File.ReadAllText(candidate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidationFailurePreservesASubstitutedStagingFile(bool throws)
    {
        using var fixture = new Fixture();
        string? substituted = null;
        var failure = Record.Exception(() => VerifiedFile.ReplaceImage(fixture.Image,
            Encoding.UTF8.GetBytes("replacement image"), path =>
            {
                substituted = path;
                File.Move(path, Path.Combine(fixture.Root, "original-staging"));
                File.WriteAllText(path, "unrelated replacement");
                if (throws) throw new IOException("validation unavailable");
                return false;
            }));

        Assert.NotNull(failure);
        Assert.NotNull(substituted);
        Assert.Equal("unrelated replacement", File.ReadAllText(substituted!));
        Assert.Equal("installed image", File.ReadAllText(fixture.Image));
    }

    [Fact]
    public void ReplacingARecordedBackupWithIdenticalBytesDoesNotTransferOwnership()
    {
        using var fixture = new Fixture();
        string backup = fixture.Publish();
        File.Move(backup, Path.Combine(fixture.Root, "original-backup"));
        File.WriteAllText(backup, "installed image");

        VerifiedFile.DeleteSetAside(fixture.Image);

        Assert.Equal("installed image", File.ReadAllText(backup));
        Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, fixture.Environment));
        Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
    }

    [Fact]
    public void ARecreatedReceiptDoesNotAuthorizeTheOriginalBackup()
    {
        using var fixture = new Fixture();
        string backup = fixture.Publish();
        foreach (string record in Directory.GetFiles(fixture.Directory, "*.recovery-*"))
        {
            string moved = Path.Combine(fixture.Root, Path.GetFileName(record));
            File.Move(record, moved);
            File.Copy(moved, record);
        }

        VerifiedFile.DeleteSetAside(fixture.Image);

        Assert.Equal("installed image", File.ReadAllText(backup));
        Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, fixture.Environment));
        Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
    }

    [Fact]
    public void ForgedReceiptAndRecoveryNamesRemainUnrelated()
    {
        using var fixture = new Fixture();
        string backup = fixture.Image + ".old-" + Guid.NewGuid().ToString("N");
        string receipt = fixture.Image + ".recovery-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(backup, "unrelated image");
        File.WriteAllText(receipt, "unrelated receipt-shaped document");

        VerifiedFile.DeleteSetAside(fixture.Image);

        Assert.Equal("unrelated image", File.ReadAllText(backup));
        Assert.Equal("unrelated receipt-shaped document", File.ReadAllText(receipt));
        Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, fixture.Environment));
    }

    [Fact]
    public void StartupRetainsAFailedCandidateUntilSuccessfulPublication()
    {
        using var fixture = new Fixture();
        using (new FileStream(fixture.Image, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => VerifiedFile.ReplaceImage(fixture.Image,
                Encoding.UTF8.GetBytes("candidate recovery")));
        string fresh = Assert.Single(Directory.GetFiles(fixture.Directory, "*.new-*"));

        VerifiedFile.DeleteSetAside(fixture.Image);

        Assert.Equal("candidate recovery", File.ReadAllText(fresh));
        Assert.Equal("installed image", File.ReadAllText(fixture.Image));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedRecoveryAndReceiptCannotAuthorizeDeletion(bool linkReceipt)
    {
        using var fixture = new Fixture();
        string backup = fixture.Publish();
        string sentinel = Path.Combine(fixture.Root, "keep.txt");
        File.WriteAllText(sentinel, "unrelated document");
        if (linkReceipt)
        {
            foreach (string record in Directory.GetFiles(fixture.Directory, "*.recovery-*"))
            {
                File.Move(record, Path.Combine(fixture.Root, Path.GetFileName(record)));
                Assert.True(CreateSymbolicLink(record, sentinel, 2), "The guest requires Developer Mode links.");
            }
        }
        else
        {
            File.Move(backup, Path.Combine(fixture.Root, "original-backup"));
            Assert.True(CreateSymbolicLink(backup, sentinel, 2), "The guest requires Developer Mode links.");
        }

        VerifiedFile.DeleteSetAside(fixture.Image);

        Assert.Equal("unrelated document", File.ReadAllText(sentinel));
        Assert.True(File.Exists(backup));
        Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, fixture.Environment));
        Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
    }

    [Fact]
    public void UnrelatedEntryRefusalDoesNotRetireAnOwnedBackup()
    {
        using var fixture = new Fixture();
        string backup = fixture.Publish();
        string unrelated = Path.Combine(fixture.Directory, "keep.txt");
        File.WriteAllText(unrelated, "unrelated document");

        Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, fixture.Environment));

        Assert.Equal("installed image", File.ReadAllText(backup));
        Assert.Equal("unrelated document", File.ReadAllText(unrelated));
        Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
    }

    [Fact]
    public void CleanupFailureLeavesTheInstallationAvailableForRetry()
    {
        using var fixture = new Fixture();
        string backup = fixture.Publish();
        using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, fixture.Environment));
            Assert.Equal("installed image", File.ReadAllText(backup));
            Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
        }

        using var removal = UninstallRemoval.Begin(false, fixture.Environment);
        removal.Commit();
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public void ImageCleanupFailureStillRollsBackAfterRecoveryReconciliation()
    {
        using var fixture = new Fixture();
        fixture.Publish();
        fixture.Environment.Cleanup = _ => throw new IOException("cleanup refused");

        using var removal = UninstallRemoval.Begin(false, fixture.Environment);
        Assert.Throws<IOException>(() => removal.Commit());

        Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MachineRemovalRejectsUntrustedRecoveryPermissions(bool receipt)
    {
        using var fixture = new Fixture(machine: true);
        string backup = fixture.Publish();
        fixture.Environment.ReadFileSecurity = path =>
        {
            var descriptor = Trusted<FileSecurity>();
            if (receipt ? path.Contains(".recovery-") : path == backup)
                descriptor.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.WriteData, AccessControlType.Allow));
            return descriptor;
        };

        Assert.Throws<UnauthorizedAccessException>(() => UninstallRemoval.Begin(true, fixture.Environment));

        Assert.Equal("installed image", File.ReadAllText(backup));
        Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLink(string link, string target, int flags);

    private static T Trusted<T>() where T : FileSystemSecurity, new()
    {
        var descriptor = new T();
        descriptor.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        return descriptor;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ks-recovery-owner-" + Guid.NewGuid().ToString("N"));
        internal string Directory { get; }
        internal string Image => Path.Combine(Directory, "KillerShell.exe");
        internal UninstallRemoval.EnvironmentBoundary Environment { get; }
        internal Fixture(bool machine = false)
        {
            Directory = Path.Combine(machine ? Root : Path.Combine(Root, "Programs"), "KillerShell");
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Image, "installed image");
            Environment = new UninstallRemoval.EnvironmentBoundary
            {
                Folder = _ => Root, Processes = () => Array.Empty<Process>(), Cleanup = File.Delete,
                ReadDirectorySecurity = _ => Trusted<DirectorySecurity>(),
                ReadFileSecurity = _ => Trusted<FileSecurity>(),
                ReadCreatorOwner = () => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            };
        }
        internal string Publish() => VerifiedFile.ReplaceImage(Image, Encoding.UTF8.GetBytes("replacement image"))!;
        public void Dispose()
        {
            string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }
}
