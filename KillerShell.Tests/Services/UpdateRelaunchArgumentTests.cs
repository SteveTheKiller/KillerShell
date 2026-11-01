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
}
