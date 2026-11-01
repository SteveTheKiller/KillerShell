using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// The kit's update helpers reopen the app with UpdateService.RelaunchArgument, empty unless the app
// sets it. KillerShell sets it to the argument that makes the reopened copy open its own window
// instead of waking a hidden one in the tray and exiting.
[Collection("UpdateRelaunchArgument")]
public sealed class UpdateRelaunchArgumentTests
{
    private static string Decompressed(ProcessStartInfo helper)
    {
        string arguments = helper.Arguments;
        int from = arguments.IndexOf("FromBase64String('", StringComparison.Ordinal) + "FromBase64String('".Length;
        byte[] packed = Convert.FromBase64String(arguments.Substring(from, arguments.IndexOf('\'', from) - from));
        using var gzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Reopen(string value)
        => "Reopen = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";

    private static string Download()
    {
        string download = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "relaunch-arg", Guid.NewGuid().ToString("N") + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        File.WriteAllText(download, "release bytes");
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(download))).Replace("-", "");
        UpdateImagePublication.RememberVerified(download, hash, new string('A', 40));
        return download;
    }

    private static void Clean(string download)
    {
        try { File.Delete(download); File.Delete(download + ".recovery.txt"); } catch { }
    }

    private static string SwapScript(string download)
    {
        ProcessStartInfo? captured = null;
        var platform = new UpdateService.Platform { CanWrite = _ => true, IsElevated = () => false, Start = psi => { captured = psi; return Process.GetCurrentProcess(); } };
        UpdateService.StartSwap(download, "1.5.1", platform);
        Assert.NotNull(captured);
        return Decompressed(captured!);
    }

    [Fact]
    public void KitDefaultReopensWithNoArgument()
    {
        string download = Download();
        try
        {
            Assert.Equal("", UpdateService.RelaunchArgument);
            Assert.Contains(Reopen(""), SwapScript(download));
        }
        finally { Clean(download); }
    }

    [Fact]
    public void UpdateHelperRelaunchesWithAnArgumentThatSkipsTrayWake()
    {
        string download = Download();
        try
        {
            TrayWake.UseForUpdateRelaunch();
            Assert.Equal(TrayWake.NoWakeArgument, UpdateService.RelaunchArgument);
            Assert.Contains(Reopen(TrayWake.NoWakeArgument), SwapScript(download));
            Assert.False(TrayWake.IsPlainLaunch(new[] { TrayWake.NoWakeArgument }));
        }
        finally { UpdateService.RelaunchArgument = ""; Clean(download); }
    }

    [Fact]
    public void MachineHelperPassesTheArgumentToBothTheUpdateAndTheFailedUpdateReopen()
    {
        string download = Download();
        try
        {
            TrayWake.UseForUpdateRelaunch();
            string target = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "KillerShell.exe");
            var helper = TrustedUpdate.Publication(download, target, "1.5.1", new string('B', 64), 1, 1, 1, 1);
            string script = Decompressed(helper);
            Assert.Contains(Reopen(TrayWake.NoWakeArgument), script);
            Assert.Contains("Start-KillerUpdatedApp ([Text.Encoding]::Unicode.GetString(", script);
            Assert.Contains("'" + TrayWake.NoWakeArgument + "' $true}catch{};exit 1}", script);
        }
        finally { UpdateService.RelaunchArgument = ""; Clean(download); }
    }

    [Fact]
    public void InstallerHelperReopensWithTheArgument()
    {
        string download = Download();
        try
        {
            TrayWake.UseForUpdateRelaunch();
            string script = UpdateImagePublication.Installer(download, "/S", false,
                Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "KillerShell.exe"), Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "stage"), "https://example.invalid/");
            Assert.Contains(Reopen(TrayWake.NoWakeArgument), script);
        }
        finally { UpdateService.RelaunchArgument = ""; Clean(download); }
    }

    // Runs the generated installer helper for real, with the publisher check passed and the installer
    // replaced by a stub that exits with the given code. The reopen is captured at the point the helper
    // would start the app, so what it records is the target and argument the helper itself computed.
    private static (int Exit, string Output, string? Reopened) RunInstallerHelper(int installerExit, bool targetExists)
    {
        string download = Download();
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "installer-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TrayWake.UseForUpdateRelaunch();
            string target = Path.Combine(root, "KillerShell.exe");
            if (targetExists) File.WriteAllText(target, "installed bytes");
            string reopened = Path.Combine(root, "reopened.txt");
            string overrides = "function Start-KillerUpdatedApp { param($t, $r, $e) [IO.File]::WriteAllText('" + reopened + "', $t + '|' + $r + '|' + $e) }\n"
                + "$ops.Publisher = { param($path, $plan) $true }\n"
                + "$ops.Run = { param($path, $arguments) " + installerExit + " }\n"
                + "$ops.Releases = { param($url) }\n";
            string script = UpdateImagePublication.Installer(download, "/S", false, target, Path.Combine(root, "stage"), "https://example.invalid/", overrides);
            string file = Path.Combine(root, "helper.ps1");
            File.WriteAllText(file, script, new UTF8Encoding(true));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            return (process.ExitCode, output, File.Exists(reopened) ? File.ReadAllText(reopened) : null);
        }
        finally
        {
            UpdateService.RelaunchArgument = "";
            try { File.Delete(download + ".update-failure.txt"); } catch { }
            Clean(download);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void InstallerHelperRunsAndReopensTheTargetWithTheArgument()
    {
        var (exit, output, reopened) = RunInstallerHelper(0, targetExists: false);
        Assert.True(exit == 0, "The installer helper did not succeed: " + output);
        Assert.NotNull(reopened);
        Assert.EndsWith("KillerShell.exe|" + TrayWake.NoWakeArgument + "|False", reopened);
    }

    [Fact]
    public void InstallerHelperReopensTheInstalledCopyWithTheArgumentWhenTheInstallerFails()
    {
        var (exit, output, reopened) = RunInstallerHelper(1, targetExists: true);
        Assert.True(exit == 1, "The installer helper should have reported the failure: " + output);
        Assert.NotNull(reopened);
        Assert.EndsWith("KillerShell.exe|" + TrayWake.NoWakeArgument + "|False", reopened);
    }

    [Fact]
    public void MachineHelperReopensAfterAFailureOnlyOnceTheOldCopyHasExited()
    {
        string download = Download();
        try
        {
            string target = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "KillerShell.exe");
            string script = Decompressed(TrustedUpdate.Publication(download, target, "1.5.1", new string('B', 64), 1, 1, 1, 1));
            int closed = script.IndexOf("$exited=$true", StringComparison.Ordinal);
            int lastWait = script.LastIndexOf("WaitForExit(60000)", StringComparison.Ordinal);
            int reopen = script.IndexOf("if(-not $exited){exit 1};try{Start-KillerUpdatedApp", StringComparison.Ordinal);
            Assert.True(closed > lastWait, "The exited flag is set only after both waits.");
            Assert.True(reopen > 0, "The failure reopen is gated by the exited flag.");
            Assert.True(reopen > closed);
        }
        finally { Clean(download); }
    }
}
