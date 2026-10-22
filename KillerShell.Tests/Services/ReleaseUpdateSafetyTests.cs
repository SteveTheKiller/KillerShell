using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ReleaseUpdateSafetyTests
{
    [Fact]
    public void StartupCleanupKeepsUnrelatedRecoveryNamedFiles()
    {
        using var fixture = new Fixture();
        string unrelated = fixture.Image + ".old-deadbeef";
        File.WriteAllText(unrelated, "unrelated document");
        VerifiedFile.DeleteSetAside(fixture.Image);
        Assert.True(File.Exists(unrelated), "Startup deleted an unowned recovery-shaped file.");
        Assert.Equal("unrelated document", File.ReadAllText(unrelated));
        Assert.Equal("installed image", File.ReadAllText(fixture.Image));
    }

    [Fact]
    public void FailedPublicationCanBeRetriedAndThenUninstalled()
    {
        using var fixture = new Fixture();
        string replacement = Path.Combine(fixture.Root, "candidate.exe");
        File.WriteAllText(replacement, "replacement image");
        using (new FileStream(fixture.Image, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => InstallationImage.Publish(replacement, fixture.Image));
            Assert.Equal("installed image", File.ReadAllText(fixture.Image));
            string recovery = Assert.Single(Directory.GetFiles(fixture.Directory, "KillerShell.exe.new-*"));
            Assert.Equal("replacement image", File.ReadAllText(recovery));
        }

        InstallationImage.Publish(replacement, fixture.Image);
        Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
        using var removal = UninstallRemoval.Begin(false, fixture.Environment);
        removal.Commit();
        Assert.False(Directory.Exists(fixture.Directory));
        Assert.Equal("replacement image", File.ReadAllText(replacement));
    }

    [Fact]
    public void AnOwnedUpdateBackupCanBeRemovedWithoutNormalStartup()
    {
        using var fixture = new Fixture();
        string? backup = VerifiedFile.ReplaceImage(fixture.Image, Encoding.UTF8.GetBytes("replacement image"));
        Assert.NotNull(backup);
        Assert.Equal("installed image", File.ReadAllText(backup!));
        using var removal = UninstallRemoval.Begin(false, fixture.Environment);
        removal.Commit();
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public void AChangedOwnedBackupIsPreserved()
    {
        using var fixture = new Fixture();
        string? backup = VerifiedFile.ReplaceImage(fixture.Image, Encoding.UTF8.GetBytes("replacement image"));
        Assert.NotNull(backup);
        File.WriteAllText(backup!, "different bytes now owned by someone else");
        VerifiedFile.DeleteSetAside(fixture.Image);
        Assert.True(File.Exists(backup!), "Cleanup deleted a backup whose identity changed.");
        Assert.Equal("different bytes now owned by someone else", File.ReadAllText(backup!));
        Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, fixture.Environment));
        Assert.Equal("replacement image", File.ReadAllText(fixture.Image));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ks-release-update-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(Root, "Programs", "KillerShell");
        internal string Image => Path.Combine(Directory, "KillerShell.exe");
        internal UninstallRemoval.EnvironmentBoundary Environment { get; }
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Image, "installed image");
            Environment = new UninstallRemoval.EnvironmentBoundary
            {
                Folder = _ => Root,
                Processes = () => Array.Empty<Process>(),
                Cleanup = File.Delete
            };
        }
        public void Dispose()
        {
            string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            System.IO.Directory.Delete(Root, true);
        }
    }
}
