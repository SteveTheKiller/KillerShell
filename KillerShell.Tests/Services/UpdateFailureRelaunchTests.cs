using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateFailureRelaunchTests
{
    // The helper holds the installed copy to the hash recorded before publication.
    private static string HashOf(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }

    // Runs the real publication helper with only the publisher check and the two process-starting
    // operations replaced, so a failed publication can be observed without opening anything.
    private static (int exit, bool relaunched, bool reported) RunFailedPublication(bool elevated, bool targetExists, bool installer,
        bool holdLease = false, bool targetChanged = false, bool realRecover = false, bool unsignedTarget = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "relaunch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Another install or update of this folder, holding its lease while the helper runs.
        using var held = holdLease ? new System.Threading.Mutex(false, KillerFamily.Deployment.InstallerExitCodes.MutexName("KillerShell", root)) : null;
        if (held != null) Assert.True(held.WaitOne(0));
        try
        {
            string download = Path.Combine(root, "download.exe");
            string target = Path.Combine(root, "KillerShell.exe");
            string relaunched = Path.Combine(root, "relaunched.txt");
            string reported = Path.Combine(root, "reported.txt");
            File.WriteAllText(download, "release bytes");
            if (targetExists) File.WriteAllText(target, "installed bytes");
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(download))).Replace("-", "");
            UpdateImagePublication.RememberVerified(download, hash, new string('A', 40));
            // realRecover keeps the helper's own Recover, with its identity checks, and a publisher
            // that passes, so the test sees whether Recover really reaches Relaunch.
            string overrides = "$ops.Publisher = { param($path, $plan) " + (realRecover ? "$true" : "$false") + " }\n"
                + "$ops.ShowReport = { param($path) [IO.File]::WriteAllText('" + reported + "', 'shown') }\n"
                + "$ops.Relaunch = { param($plan) [IO.File]::WriteAllText('" + relaunched + "', 'started') }\n"
                + (realRecover ? "" : "$ops.Recover = { param($plan) & $ops.Relaunch $plan }\n") + "$ops.RelaunchInstalled = { param($plan) & $ops.Relaunch $plan }\n"
                + "$ops.Releases = { param($url) }\n"
                // The stand-in installed copy carries no signature, so its certificate identity is replaced too.
                + "$ops.VerifiedTargetThumb = { param($path, $signer) '" + (unsignedTarget ? "" : new string('A', 40)) + "' }\n";
            string script = installer
                ? UpdateImagePublication.Installer(download, "/S", elevated, target, Path.Combine(root, "stage"), "https://example.invalid/", overrides)
                : UpdateImagePublication.Script(download, target, elevated, "https://example.invalid/", "KillerShell", showReport: true, overrides: overrides,
                    originalHash: targetExists && !targetChanged ? HashOf("installed bytes") : new string('C', 64));
            string file = Path.Combine(root, "helper.ps1");
            File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            return (process.ExitCode, File.Exists(relaunched), File.Exists(reported));
        }
        finally
        {
            held?.ReleaseMutex();
            Directory.Delete(root, true);
        }
    }

    // The app has already closed when the helper finds another install holding the folder, or the
    // installed copy changed since the update started. The update is not applied, and the copy that
    // is there is started again instead of leaving the user with nothing running.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABusyFolderReopensTheInstalledCopy(bool elevated)
    {
        // The helper's own Recover, as the app calls it: no installed signer is passed in.
        var (exit, relaunched, _) = RunFailedPublication(elevated, targetExists: true, installer: false, holdLease: true, realRecover: true);
        Assert.Equal(1618, exit);
        Assert.True(relaunched, "The installed copy must be started again when another install holds the folder.");
    }

    // Reopening is for a copy that carries the publisher's signature. One that changed into
    // something unsigned is left closed.
    [Fact]
    public void AChangedUnsignedCopyIsNotStarted()
    {
        var (exit, relaunched, _) = RunFailedPublication(false, targetExists: true, installer: false, targetChanged: true, realRecover: true, unsignedTarget: true);
        Assert.Equal(1618, exit);
        Assert.False(relaunched, "An unsigned installed copy must not be started.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChangedInstalledCopyIsReopenedNotReplaced(bool elevated)
    {
        var (exit, relaunched, _) = RunFailedPublication(elevated, targetExists: true, installer: false, targetChanged: true, realRecover: true);
        Assert.Equal(1618, exit);
        Assert.True(relaunched, "The installed copy must be started again when it changed before publication.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedPublicationRelaunchesTheExistingCopy(bool elevated, bool installer)
    {
        var (exit, relaunched, reported) = RunFailedPublication(elevated, targetExists: true, installer);
        Assert.Equal(1, exit);
        Assert.True(relaunched, "The existing copy must be started again after a failed update.");
        if (!installer && !elevated) Assert.True(reported);
    }

    [Fact]
    public void FailedPublicationWithNoTargetStartsNothing()
    {
        var (exit, relaunched, _) = RunFailedPublication(elevated: false, targetExists: false, installer: false);
        // 1618: the installed image the helper recorded is missing, so the update is not applied.
        Assert.Equal(1618, exit);
        Assert.False(relaunched);
    }
}
