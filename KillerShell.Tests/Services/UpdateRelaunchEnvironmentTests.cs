using System;
using System.Diagnostics;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateRelaunchEnvironmentTests
{
    // The helper strips PSModulePath down to Windows PowerShell's own folder so a planted module
    // cannot run inside it. The app it reopens must get the user's value back, not that stripped one.
    [Fact]
    public void RelaunchedAppGetsTheOriginalModulePathNotTheHelpersStrippedOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "relaunch-env", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string probe = Path.Combine(root, "probe.cmd");
            string output = Path.Combine(root, "modulepath.txt");
            File.WriteAllText(probe, "@echo off\r\n>\"%~dp0modulepath.txt\" echo %PSModulePath%\r\n");
            string sentinel = @"C:\KillerShellSentinel\Modules;C:\KillerShellSentinel\More";
            string script = UpdateImagePublication.Guard + "\n[KillerUpdateGuard]::Launch('" + probe.Replace("'", "''") + "', '', $false)\n";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                ImmutablePowerShell.Arguments(script))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.EnvironmentVariables["PSModulePath"] = sentinel;
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            Assert.True(process.ExitCode == 0, errors.Result);
            var end = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(output) && DateTime.UtcNow < end) System.Threading.Thread.Sleep(50);
            System.Threading.Thread.Sleep(200);
            Assert.True(File.Exists(output), "The relaunched probe never ran.");
            Assert.Equal(sentinel, File.ReadAllText(output).Trim());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
