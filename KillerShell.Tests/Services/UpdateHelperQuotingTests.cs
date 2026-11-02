using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

[Collection("UpdateRelaunchArgument")]
public sealed class UpdateHelperQuotingTests
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

    private static string Download(string folderName)
    {
        string download = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "quoting", folderName + Guid.NewGuid().ToString("N"), "update.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        File.WriteAllText(download, "release bytes");
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(download))).Replace("-", "");
        UpdateImagePublication.RememberVerified(download, hash, new string('A', 40));
        return download;
    }

    private static void Clean(string download)
    {
        try { Directory.Delete(Path.GetDirectoryName(download)!, true); } catch { }
    }

    [Theory]
    [InlineData("O'Neil")]
    [InlineData("O\u2018Neil")]
    [InlineData("O\u2019Neil")]
    [InlineData("O\u201AExample")]
    [InlineData("O\u201BExample")]
    public void TheNotClosedReportCommandParsesAndNamesTheReport(string folder)
    {
        string report = @"C:\Users\" + folder + @"\update.exe.recovery.txt";
        string text = UpdateService.NotClosed(report);

        var parsed = PowerShellParse.Parse(text);

        Assert.Null(parsed.Error);
        // The path travels as data, so it is never a literal the quote characters could end.
        Assert.DoesNotContain(folder, text, StringComparison.Ordinal);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(report));
        Assert.Contains(encoded, parsed.Strings);
    }

    [Theory]
    [InlineData("O\u2019Neil")]
    [InlineData("O'Neil")]
    public void ThePerUserHelperParsesWhenTheDownloadFolderHoldsAQuote(string folder)
    {
        string download = Download(folder);
        try
        {
            ProcessStartInfo? captured = null;
            var platform = new UpdateService.Platform { CanWrite = _ => true, IsElevated = () => false, Start = psi => { captured = psi; return Process.GetCurrentProcess(); } };
            UpdateService.StartSwap(download, "1.5.1", platform);
            string script = Decompressed(captured!);

            var parsed = PowerShellParse.Parse(script);

            Assert.Null(parsed.Error);
        }
        finally { Clean(download); }
    }

    [Theory]
    [InlineData("O\u2019Neil")]
    public void TheLauncherHelperParsesWhenTheDownloadFolderHoldsAQuote(string folder)
    {
        string download = Download(folder);
        try
        {
            var target = new UpdateService.UpdateTarget { Replace = Path.Combine(Path.GetDirectoryName(download)!, "Launcher.exe") };
            string script = UpdateService.PackageHelperScript(download, target.Replace!, target, elevate: false, 1, 1);

            Assert.Null(PowerShellParse.Parse(script).Error);
        }
        finally { Clean(download); }
    }

    private static string SwapScript(string download, bool elevated)
    {
        ProcessStartInfo? captured = null;
        var platform = new UpdateService.Platform { CanWrite = _ => true, IsElevated = () => elevated, Start = psi => { captured = psi; return Process.GetCurrentProcess(); } };
        UpdateService.StartSwap(download, "1.5.1", platform);
        Assert.NotNull(captured);
        return Decompressed(captured!);
    }

    [Fact]
    public void AnElevatedPerUserHelperReopensTheAppThroughTheDeElevatedPath()
    {
        string download = Download("elevated");
        try
        {
            string script = SwapScript(download, elevated: true);
            Assert.Contains("Elevated = $true", script, StringComparison.Ordinal);
            Assert.Contains("ShowReport = $false", script, StringComparison.Ordinal);
            // An administrator's helper never opens a window of its own as administrator.
            Assert.DoesNotContain("did not close within a minute", script, StringComparison.Ordinal);
        }
        finally { Clean(download); }
    }

    [Fact]
    public void AnUnelevatedPerUserHelperKeepsItsReportAndPlainRelaunch()
    {
        string download = Download("plain");
        try
        {
            string script = SwapScript(download, elevated: false);
            Assert.Contains("Elevated = $false", script, StringComparison.Ordinal);
            Assert.Contains("ShowReport = $true", script, StringComparison.Ordinal);
            Assert.Contains("did not close within a minute", script, StringComparison.Ordinal);
        }
        finally { Clean(download); }
    }
}
