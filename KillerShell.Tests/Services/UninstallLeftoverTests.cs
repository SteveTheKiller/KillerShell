using System;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UninstallLeftoverTests
{
    private static (string root, string directory, UninstallRemoval.EnvironmentBoundary environment) Fixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "leftover", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "Programs", "KillerShell");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "KillerShell.exe"), "image");
        return (root, directory, new UninstallRemoval.EnvironmentBoundary { Folder = _ => root, Cleanup = File.Delete });
    }

    [Fact]
    public void UpdaterLeftoversAreRemovedWithTheInstallation()
    {
        var (root, directory, environment) = Fixture();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            string[] names =
            {
                "KillerShell.exe.old-" + id, "KillerShell.exe.new-" + id, "KillerShell.exe.restore-" + id,
                ".update-" + id + ".exe", ".update-" + id + ".exe.recovery.txt", "KillerShell.exe." + id + ".bak",
            };
            foreach (string name in names) File.WriteAllText(Path.Combine(directory, name), "x");
            using (var removal = UninstallRemoval.Begin(false, environment)) removal.Commit();
            Assert.False(Directory.Exists(directory));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void KeptRecoveryCopiesAndUnrelatedFilesStillRefuse()
    {
        var (root, directory, environment) = Fixture();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            string kept = Path.Combine(directory, "KillerShell.exe." + id + ".bak");
            File.WriteAllText(kept, "x");
            File.WriteAllText(kept + ".keep", "");
            Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, environment));
            File.Delete(kept); File.Delete(kept + ".keep");
            string other = Path.Combine(directory, "notes.txt");
            File.WriteAllText(other, "mine");
            Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, environment));
            Assert.True(File.Exists(other));
        }
        finally { Directory.Delete(root, true); }
    }
}
