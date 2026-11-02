using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateRestoreRecoveryTests
{
    private sealed class Outcome
    {
        internal int Exit;
        internal string Target = "";
        internal string? Relaunched;
        internal string? Report;
        internal bool BackupLeft;
        internal int Attempts;
        internal string Output = "";
    }

    // Runs the real publication helper with the publisher check made to fail on the published image
    // (or not), plus the given operation overrides.
    private static Outcome Run(string overrides, bool installed = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "restore-recovery", Guid.NewGuid().ToString("N"));
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
            string all = overrides
                + "$ops.Relaunch = { param($plan) $t = if ([IO.File]::Exists($plan.Target)) { [IO.File]::ReadAllText($plan.Target) } else { '' }; [IO.File]::WriteAllText('" + relaunched + "', $t) }\n"
                + "$ops.Recover = { param($plan) & $ops.Relaunch $plan }\n$ops.RelaunchInstalled = { param($plan) & $ops.Relaunch $plan }\n";
            string script = UpdateImagePublication.Script(download, target, false, "https://example.invalid/", "KillerShell", overrides: all);
            string file = Path.Combine(root, "helper.ps1");
            File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            string log = Path.Combine(root, "attempts.txt");
            string report = download + ".recovery.txt";
            return new Outcome
            {
                Exit = process.ExitCode,
                Target = File.Exists(target) ? File.ReadAllText(target) : "",
                Relaunched = File.Exists(relaunched) ? File.ReadAllText(relaunched) : null,
                Report = File.Exists(report) ? File.ReadAllText(report) : null,
                BackupLeft = Directory.GetFiles(root, "*.old-*").Length > 0,
                Attempts = File.Exists(log) ? File.ReadAllText(log).Length : 0,
                Output = output
            };
        }
        finally { Directory.Delete(root, true); }
    }

    private const string PublisherRejectsTarget = "$ops.Publisher = { param($path, $plan) $path -ne $plan.Target }\n";
    private const string Busy = "(New-Object IO.IOException('busy', -2147024864))";

    // While the failure is being undone the helper believes the target is gone, which is the state a
    // swap that died halfway leaves behind.
    private const string TargetLooksGone = "$realExists = $ops.Exists\n"
        + "$ops.Exists = { param($p) if ($plan.Unsafe -and $p -eq $plan.Target) { $false } else { & $realExists $p } }\n";

    [Fact]
    public void ALeftoverRestorationFileDoesNotStopTheRestore()
    {
        var outcome = Run(PublisherRejectsTarget
            + "$realRestore = $ops.Restore\n"
            + "$ops.Restore = { param($backup, $target, $restore) [IO.File]::WriteAllText($restore, 'stale'); & $realRestore $backup $target $restore }\n");
        Assert.Equal(1, outcome.Exit);
        Assert.Equal("installed bytes", outcome.Target);
        Assert.Equal("installed bytes", outcome.Relaunched);
    }

    [Fact]
    public void MovingTheRestoredImageIntoPlaceRetriesASharingViolation()
    {
        var outcome = Run(PublisherRejectsTarget + TargetLooksGone
            + "$ops.Move = { param($source, $target)\n"
            + "  $log = Join-Path ([IO.Path]::GetDirectoryName($plan.Target)) 'attempts.txt'\n"
            + "  [IO.File]::AppendAllText($log, 'x')\n"
            + "  if ((Get-Content $log -Raw).Length -le 2) { throw " + Busy + " }\n"
            + "  [IO.File]::Copy($source, $target, $true); [IO.File]::Delete($source) }\n");
        Assert.Equal(1, outcome.Exit);
        Assert.Equal(3, outcome.Attempts);
        Assert.Equal("installed bytes", outcome.Target);
        Assert.Equal("installed bytes", outcome.Relaunched);
    }

    [Fact]
    public void TheReportKeepsBothRestorationFailures()
    {
        var outcome = Run(PublisherRejectsTarget + TargetLooksGone
            + "$global:tries = 0\n"
            + "$ops.Restore = { param($backup, $target, $restore) $global:tries++; throw ('restore-failure-' + $global:tries) }\n");
        Assert.Equal(1, outcome.Exit);
        Assert.NotNull(outcome.Report);
        Assert.Contains("restore-failure-1", outcome.Report, StringComparison.Ordinal);
        Assert.Contains("restore-failure-2", outcome.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegistryFailureAfterPublishingStillFinishesTheUpdateAndKeepsAWarning()
    {
        var outcome = Run("$ops.Publisher = { param($path, $plan) $true }\n"
            + "$ops.Metadata = { param($plan) throw 'registry blocked' }\n");
        Assert.Equal(0, outcome.Exit);
        Assert.Equal("release bytes", outcome.Target);
        Assert.Equal("release bytes", outcome.Relaunched);
        Assert.False(outcome.BackupLeft);
        Assert.NotNull(outcome.Report);
        Assert.Contains("registry blocked", outcome.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulUpdateStillRemovesItsReport()
    {
        var outcome = Run("$ops.Publisher = { param($path, $plan) $true }\n");
        Assert.Equal(0, outcome.Exit);
        Assert.Equal("release bytes", outcome.Relaunched);
        Assert.Null(outcome.Report);
    }
}
