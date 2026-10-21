using System;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class InstallationImageTests
{
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
            string backup = Assert.Single(Directory.GetFiles(root, "KillerShell.exe.old-*"));
            Assert.Equal(previous, File.ReadAllBytes(backup));
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
