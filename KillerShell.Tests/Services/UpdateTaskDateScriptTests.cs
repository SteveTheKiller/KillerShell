using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// The reopen task the elevated helper registers carries a registration date, and the helper's own
// sweep of older tasks leaves a recent one alone. The script's two functions run for real, with
// no task registered.
public sealed class UpdateTaskDateScriptTests
{
    [Fact]
    public void TheHelperStampsACurrentDateAndItsSweepKeepsOnlyRecentTasks()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "taskdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string download = Path.Combine(root, "download.exe");
            File.WriteAllText(download, "release bytes");
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(download))).Replace("-", "");
            UpdateImagePublication.RememberVerified(download, hash, new string('A', 40));
            string result = Path.Combine(root, "result.txt");
            string overrides = "$stamp = New-KillerUpdateTaskDate\n"
                + "$parsed = [DateTime]::Parse($stamp, [Globalization.CultureInfo]::InvariantCulture)\n"
                + "$fresh = [Math]::Abs(((Get-Date) - $parsed).TotalSeconds) -lt 60\n"
                + "$recent = Test-KillerUpdateTaskRecent ((Get-Date).AddMinutes(-2).ToString('s')) (Get-Date)\n"
                + "$old = Test-KillerUpdateTaskRecent ((Get-Date).AddMinutes(-10).ToString('s')) (Get-Date)\n"
                + "$blank = Test-KillerUpdateTaskRecent '' (Get-Date)\n"
                + "$odd = Test-KillerUpdateTaskRecent 'not a date' (Get-Date)\n"
                + "[IO.File]::WriteAllText('" + result + "', ($fresh, $recent, $old, $blank, $odd) -join ','); exit 0\n";
            string script = UpdateImagePublication.Script(download, Path.Combine(root, "KillerShell.exe"), false, "https://example.invalid/", "KillerShell", overrides: overrides);
            string file = Path.Combine(root, "helper.ps1");
            File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            Assert.True(File.Exists(result), "The helper functions did not run: " + output);
            Assert.Equal("True,True,False,False,False", File.ReadAllText(result));
        }
        finally { Directory.Delete(root, true); }
    }
}
