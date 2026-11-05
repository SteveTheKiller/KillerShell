using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateRestoreAfterReplaceTests
{
    // Runs the real publication helper with the checks on the published image made to fail after
    // the Replace, and reports what is left on disk and what the helper started again.
    private int attempts;

    // The helper holds the installed copy to the hash recorded before publication.
    private static string HashOf(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }

    private (int exit, string targetText, string? relaunchedText) RunPublication(string overrides, bool installed = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string download = Path.Combine(root, "download.exe");
            string target = Path.Combine(root, "KillerShell.exe");
            string relaunched = Path.Combine(root, "relaunched.txt");
            File.WriteAllText(download, "release bytes");
            if (installed) File.WriteAllText(target, "installed bytes");
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(download))).Replace("-", "");
            UpdateImagePublication.RememberVerified(download, hash, new string('A', 40));
            string replaced = Path.Combine(root, "replaced.txt");
            string all = "$realReplace = $ops.Replace\n"
                + "$ops.Replace = { param($source, $target, $backup) & $realReplace $source $target $backup; [IO.File]::WriteAllText('" + replaced + "', 'x') }\n"
                + "$realMove = $ops.Move\n"
                + "$ops.Move = { param($source, $target) & $realMove $source $target; [IO.File]::WriteAllText('" + replaced + "', 'x') }\n"
                + overrides
                // The stand-in installed copy carries no signature, so its certificate identity is replaced
                // too: recovery reopens only an image whose identity was recorded before publication.
                + "$ops.VerifiedTargetThumb = { param($path, $signer) '" + new string('A', 40) + "' }\n"
                + "$ops.Relaunch = { param($plan) $t = if ([IO.File]::Exists($plan.Target)) { [IO.File]::ReadAllText($plan.Target) } else { '' }; [IO.File]::WriteAllText('" + relaunched + "', $t) }\n"
                + "$ops.Recover = { param($plan) & $ops.Relaunch $plan }\n$ops.RelaunchInstalled = { param($plan) & $ops.Relaunch $plan }\n";
            string script = UpdateImagePublication.Script(download, target, false, "https://example.invalid/", "KillerShell", overrides: all,
                originalHash: installed ? HashOf("installed bytes") : new string('C', 64));
            string file = Path.Combine(root, "helper.ps1");
            File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            Assert.True(File.Exists(replaced), "The helper never reached the Replace: " + output);
            string log = Path.Combine(root, "attempts.txt");
            attempts = File.Exists(log) ? File.ReadAllText(log).Length : 0;
            return (process.ExitCode, File.Exists(target) ? File.ReadAllText(target) : "", File.Exists(relaunched) ? File.ReadAllText(relaunched) : null);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PublisherFailureAfterReplaceRestoresTheBackupAndRelaunchesOnlyIt()
    {
        var (exit, target, relaunched) = RunPublication("$ops.Publisher = { param($path, $plan) $path -ne $plan.Target }\n");
        Assert.Equal(1, exit);
        Assert.Equal("installed bytes", target);
        Assert.Equal("installed bytes", relaunched);
    }

    [Fact]
    public void HashFailureAfterReplaceRestoresTheBackupAndRelaunchesOnlyIt()
    {
        var (exit, target, relaunched) = RunPublication("$ops.Publisher = { param($path, $plan) $true }\n$ops.HashStream = { param($stream) $global:calls++; $h = [Security.Cryptography.SHA256]::Create(); try { $stream.Position = 0; $sum = [BitConverter]::ToString($h.ComputeHash($stream)).Replace('-', ''); if ($global:calls -ge 4) { 'BAD' } else { $sum } } finally { $h.Dispose() } }\n");
        Assert.Equal(1, exit);
        Assert.Equal("installed bytes", target);
        Assert.Equal("installed bytes", relaunched);
    }

    [Fact]
    public void FailedRestoreNeverRelaunchesTheUnverifiedImage()
    {
        var (exit, _, relaunched) = RunPublication("$ops.Publisher = { param($path, $plan) $path -ne $plan.Target }\n"
            + "$ops.Restore = { param($backup, $target, $restore) throw 'restore blocked' }\n");
        Assert.Equal(1, exit);
        Assert.Null(relaunched);
    }

    [Fact]
    public void MetadataFailureKeepsThePublishedImageAndRelaunchesIt()
    {
        var (exit, target, relaunched) = RunPublication("$ops.Publisher = { param($path, $plan) $true }\n"
            + "$ops.Metadata = { param($plan) throw 'registry blocked' }\n");
        Assert.Equal(0, exit);
        Assert.Equal("release bytes", target);
        Assert.Equal("release bytes", relaunched);
    }

    private const string Busy = "(New-Object IO.IOException('busy', -2147024864))";
    private const string Attempt = "[IO.File]::AppendAllText((Join-Path ([IO.Path]::GetDirectoryName($plan.Target)) 'attempts.txt'), 'x'); ";

    [Fact]
    public void RestoreRetriesASharingViolationBeforeGivingUp()
    {
        var (exit, target, relaunched) = RunPublication("$ops.Publisher = { param($path, $plan) $path -ne $plan.Target }\n"
            + "$realSwap = $ops.Swap\n"
            + "$ops.Swap = { param($source, $target, $backup) if (-not $backup) { " + Attempt + "if ((Get-Content (Join-Path ([IO.Path]::GetDirectoryName($plan.Target)) 'attempts.txt') -Raw).Length -le 2) { throw " + Busy + " } }; & $realSwap $source $target $backup }\n");
        Assert.Equal(1, exit);
        Assert.Equal(3, attempts);
        Assert.Equal("installed bytes", target);
        Assert.Equal("installed bytes", relaunched);
    }

    [Fact]
    public void APublicationWithNoInstalledCopyIsRefusedBeforeAnythingIsReplaced()
    {
        // The helper records the installed image before the app closes, so a missing one cannot be
        // published over: the update is refused (1618) and nothing is replaced or started.
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string download = Path.Combine(root, "download.exe");
            string target = Path.Combine(root, "KillerShell.exe");
            File.WriteAllText(download, "release bytes");
            UpdateImagePublication.RememberVerified(download, HashOf("release bytes"), new string('A', 40));
            string script = UpdateImagePublication.Script(download, target, false, "https://example.invalid/", "KillerShell",
                originalHash: new string('C', 64));
            string file = Path.Combine(root, "helper.ps1");
            File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            Assert.Equal(1618, process.ExitCode);
            Assert.False(File.Exists(target));
        }
        finally { Directory.Delete(root, true); }
    }
}
