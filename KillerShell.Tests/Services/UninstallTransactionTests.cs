using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UninstallTransactionTests
{
    [Fact]
    public void OnlyAuthenticatedHeldCoordinatorIsExemptFromRunningImageCheck()
    {
        string root = NewRoot();
        string directory = Path.Combine(root, "Programs", "KillerShell");
        string image = Path.Combine(directory, "KillerShell.exe");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(root, "Waiter.cs");
        File.WriteAllText(source, "class Waiter { static void Main() { System.Console.WriteLine(\"ready\"); System.Console.ReadLine(); } }");
        using var compiler = Process.Start(new ProcessStartInfo(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "csc.exe"),
            "/nologo /target:exe /out:\"" + image + "\" \"" + source + "\"") { UseShellExecute=false, CreateNoWindow=true })!;
        Assert.True(compiler.WaitForExit(15000));
        Assert.Equal(0, compiler.ExitCode);
        Process Start() => Process.Start(new ProcessStartInfo(image) {UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true})!;
        using var coordinator = Start();
        using var other = Start();
        try
        {
            Assert.Equal("ready", coordinator.StandardOutput.ReadLine());
            Assert.Equal("ready", other.StandardOutput.ReadLine());
            var environment = new UninstallRemoval.EnvironmentBoundary
            {
                Folder = _ => root,
                Processes = () => new[] {Process.GetProcessById(coordinator.Id), Process.GetProcessById(other.Id)},
            };
            var busy = Assert.Throws<System.ComponentModel.Win32Exception>(() => UninstallRemoval.Begin(false, environment, coordinator));
            Assert.Equal(KillerFamily.Deployment.InstallerExitCodes.Busy, busy.NativeErrorCode);
            other.StandardInput.WriteLine();
            Assert.True(other.WaitForExit(5000));
            environment.Processes = () => new[] {Process.GetProcessById(coordinator.Id)};
            Assert.Throws<System.ComponentModel.Win32Exception>(() => UninstallRemoval.Begin(false, environment));
            using (UninstallRemoval.Begin(false, environment, coordinator)) Assert.False(File.Exists(image));
            Assert.True(File.Exists(image));
            Assert.False(coordinator.HasExited);
        }
        finally
        {
            foreach (var process in new[] {coordinator, other})
                if (!process.HasExited) {process.Kill(); process.WaitForExit();}
            DeleteOwnedRoot(root);
        }
    }

    [Fact]
    public void FailureAfterImageRemovalKeepsExecutableAvailableForRetry()
    {
        string root = NewRoot();
        string directory = Path.Combine(root, "Programs", "KillerShell");
        string image = Path.Combine(directory, "KillerShell.exe");
        Directory.CreateDirectory(directory);
        File.WriteAllText(image, "original executable bytes");
        string icon = Path.Combine(directory, "text-file.ico");
        File.WriteAllBytes(icon, new byte[] {1,2,3,4});
        var environment = new UninstallRemoval.EnvironmentBoundary
        {
            Folder = _ => root,
            Cleanup = File.Delete,
        };
        try
        {
            using (UninstallRemoval.Begin(false, environment))
            {
                Assert.False(File.Exists(image));
                // Required integration cleanup fails, so the transaction is not committed.
            }
            Assert.True(File.Exists(image), "A failed uninstall must retain its executable for retry.");
            Assert.Equal(new byte[] {1,2,3,4}, File.ReadAllBytes(icon));
        }
        finally
        {
            DeleteOwnedRoot(root);
        }
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("launch-failure")]
    [InlineData("replacement")]
    [InlineData("unrelated")]
    [InlineData("untrusted-machine")]
    public void PublicationAndRecoveryPreserveOnlyOwnedFiles(string scenario)
    {
        string root = NewRoot();
        string parent = scenario == "untrusted-machine" ? root : Path.Combine(root, "Programs");
        string directory = Path.Combine(parent, "KillerShell");
        string image = Path.Combine(directory, "KillerShell.exe");
        string icon = Path.Combine(directory, "text-file.ico");
        Directory.CreateDirectory(directory);
        File.WriteAllText(image, "original executable bytes");
        File.WriteAllBytes(icon, new byte[] {1,2,3,4});
        string sibling = Path.Combine(parent, "unrelated.txt");
        File.WriteAllText(sibling, "preserve sibling");
        bool cleanupStarted = false;
        var environment = new UninstallRemoval.EnvironmentBoundary
        {
            Folder = _ => root,
            Cleanup = owned =>
            {
                cleanupStarted = true;
                if (scenario == "launch-failure") throw new InvalidOperationException("launch refused");
                File.Delete(owned);
            }
        };
        try
        {
            if (scenario == "unrelated")
            {
                File.WriteAllText(Path.Combine(directory, "keep.txt"), "preserve interior file");
                Assert.Throws<IOException>(() => UninstallRemoval.Begin(false, environment));
                Assert.Equal("original executable bytes", File.ReadAllText(image));
                Assert.False(cleanupStarted);
                return;
            }
            if (scenario == "untrusted-machine")
            {
                Assert.Throws<UnauthorizedAccessException>(() => UninstallRemoval.Begin(true, environment));
                Assert.Equal("original executable bytes", File.ReadAllText(image));
                Assert.False(cleanupStarted);
                return;
            }
            using var removal = UninstallRemoval.Begin(false, environment);
            Assert.False(Directory.Exists(directory));
            Assert.False(cleanupStarted);
            string recovery = Assert.IsType<string>(removal.RecoveryImagePath);
            Assert.Equal("original executable bytes", File.ReadAllText(recovery));
            if (scenario == "replacement")
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(image, "replacement installation");
                var failure = Assert.Throws<IOException>(() => removal.Rollback(new InvalidOperationException("metadata refused")));
                Assert.IsType<AggregateException>(failure.InnerException);
                Assert.Contains("metadata refused", failure.Message);
                Assert.Equal("replacement installation", File.ReadAllText(image));
                Assert.Equal(recovery, removal.RecoveryImagePath);
                Assert.Equal("original executable bytes", File.ReadAllText(recovery));
            }
            else if (scenario == "launch-failure")
            {
                Assert.Throws<InvalidOperationException>(() => removal.Commit());
                Assert.Equal("original executable bytes", File.ReadAllText(image));
                Assert.Equal(new byte[] {1,2,3,4}, File.ReadAllBytes(icon));
            }
            else
            {
                removal.Commit();
                Assert.True(cleanupStarted);
                Assert.False(Directory.Exists(directory));
                Assert.False(File.Exists(recovery));
            }
            Assert.Equal("preserve sibling", File.ReadAllText(sibling));
        }
        finally
        {
            DeleteOwnedRoot(root);
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "KillerShell-uninstall-" + Guid.NewGuid().ToString("N"));
    private static void DeleteOwnedRoot(string root)
    {
        string full = Path.GetFullPath(root);
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string name = Path.GetFileName(full);
        const string prefix = "KillerShell-uninstall-";
        if (!string.Equals(Path.GetDirectoryName(full), temporary, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name.Substring(prefix.Length), "N", out _)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The uninstall fixture escaped its owned temporary root.");
        Directory.Delete(full, true);
    }
}
