using System;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class InstallationImageTests
{
    // The Install button takes the same per-folder lease as the silent install and the update
    // helpers, so an install and an update of one folder never publish over each other.
    [Fact]
    public void TheInteractiveInstallRefusesWhileTheFolderIsLeased()
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "lease-" + Guid.NewGuid().ToString("N"));
        using var held = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        var holder = new System.Threading.Thread(() =>
        {
            using (KillerFamily.Deployment.InstallerExitCodes.Acquire("KillerShell", folder))
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        });
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            bool installed = false;
            Assert.ThrowsAny<Exception>(() => App.InstallUnderLease(folder, () => installed = true));
            Assert.False(installed);
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        bool ran = false;
        App.InstallUnderLease(folder, () => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void LockedBackupStillPublishesAndRetainsBothImages()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "candidate.exe");
        string target = Path.Combine(root, "KillerShell.exe");
        File.WriteAllText(source, "complete candidate");
        File.WriteAllText(target, "previous image");
        string? backup = null;
        try
        {
            // The backup is held open, as the image of a still-running previous copy is, so it
            // cannot be retired. Publication must still succeed and report the leftover.
            string? warning = InstallationImage.Publish(source, target, path =>
            {
                backup = path;
                using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                File.Delete(path);
            });

            Assert.NotNull(backup);
            Assert.Equal("previous image", File.ReadAllText(backup!));
            Assert.Equal("complete candidate", File.ReadAllText(target));
            Assert.Equal("complete candidate", File.ReadAllText(source));
            Assert.NotNull(warning);
            Assert.Contains("Recovery image: " + backup, warning);
            Assert.Contains("Installation image was published", warning);

            // Once the old copy has exited, the next publish retires the backup.
            Assert.Null(InstallationImage.Publish(source, target));
            Assert.False(File.Exists(backup), "The retained backup was not retired once it was free.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailedInstallationCopyPreservesThePreviousExecutable()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "candidate.exe");
        string target = Path.Combine(root, "KillerShell.exe");
        byte[] previous = System.Text.Encoding.ASCII.GetBytes(new string('O', 8192));
        byte[] candidate = System.Text.Encoding.ASCII.GetBytes(new string('N', 16384));
        try
        {
            File.WriteAllBytes(source, candidate);
            File.WriteAllBytes(target, previous);
            using (var held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                held.Lock(4096, 4096);
                try { Assert.ThrowsAny<IOException>(() => InstallationImage.Publish(source, target)); }
                finally { held.Unlock(4096, 4096); }
            }
            Assert.Equal(previous, File.ReadAllBytes(target));
            string recovery = Assert.Single(Directory.GetFiles(root, "KillerShell.exe.new-*"));
            Assert.Equal(candidate, File.ReadAllBytes(recovery));

            InstallationImage.Publish(source, target);

            Assert.Equal(candidate, File.ReadAllBytes(target));
            Assert.Empty(Directory.GetFiles(root, "KillerShell.exe.old-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FreshInstallationPublishesTheCompleteCandidate()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "candidate.exe");
            string target = Path.Combine(root, "KillerShell.exe");
            File.WriteAllText(source, "complete candidate image");

            InstallationImage.Publish(source, target);

            Assert.Equal("complete candidate image", File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(root, "KillerShell.exe.new-*"));
            Assert.Empty(Directory.GetFiles(root, "KillerShell.exe.old-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
