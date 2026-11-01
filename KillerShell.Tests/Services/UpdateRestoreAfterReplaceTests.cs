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
    private static (int exit, string targetText, string? relaunchedText) RunPublication(string overrides)
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string download = Path.Combine(root, "download.exe");
            string target = Path.Combine(root, "KillerShell.exe");
            string relaunched = Path.Combine(root, "relaunched.txt");
            File.WriteAllText(download, "release bytes");
            File.WriteAllText(target, "installed bytes");
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(download))).Replace("-", "");
            UpdateImagePublication.RememberVerified(download, hash, new string('A', 40));
            string replaced = Path.Combine(root, "replaced.txt");
            string all = "$realReplace = $ops.Replace\n"
                + "$ops.Replace = { param($source, $target, $backup) & $realReplace $source $target $backup; [IO.File]::WriteAllText('" + replaced + "', 'x') }\n"
                + overrides
                + "$ops.Relaunch = { param($plan) $t = if ([IO.File]::Exists($plan.Target)) { [IO.File]::ReadAllText($plan.Target) } else { '' }; [IO.File]::WriteAllText('" + relaunched + "', $t) }\n";
            string script = UpdateImagePublication.Script(download, target, false, "https://example.invalid/", "KillerShell", overrides: all);
            string file = Path.Combine(root, "helper.ps1");
            File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            Assert.True(File.Exists(replaced), "The helper never reached the Replace: " + output);
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
        var (exit, target, relaunched) = RunPublication("$ops.Publisher = { param($path, $plan) $true }\n$ops.HashStream = { param($stream) $global:calls++; $h = [Security.Cryptography.SHA256]::Create(); try { $stream.Position = 0; $sum = [BitConverter]::ToString($h.ComputeHash($stream)).Replace('-', ''); if ($global:calls -ge 2) { 'BAD' } else { $sum } } finally { $h.Dispose() } }\n");
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
        Assert.Equal(1, exit);
        Assert.Equal("release bytes", target);
        Assert.Equal("release bytes", relaunched);
    }
}
