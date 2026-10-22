using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using KillerShell.Tests.Safety;
using Xunit;

namespace KillerShell.Tests.Modules;

public sealed class ScriptLifetimeTests
{
    [DisposableVmFact]
    [Trait("Category", "DisposableVm")]
    public Task WindowsPowerShellKeepsDetachedScriptVersions()
    {
        DisposableVmGuard.RequireDisposableVm();
        return RunFixture("powershell.exe");
    }

    [DisposableVmFact]
    [Trait("Category", "DisposableVm")]
    public Task PowerShellSevenKeepsDetachedScriptVersions()
    {
        DisposableVmGuard.RequireDisposableVm();
        return RunFixture("pwsh.exe");
    }

    private static async Task RunFixture(string host)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string repo = directory!.FullName;
        string fixture = Path.Combine(repo, "KillerShell.Tests", "Modules", "KillerScripts", "Tests", "DetachedLifetimeSafety.ps1");
        string module = Path.Combine(repo, "Modules", "KillerScripts", "KillerScripts.psd1");
        string scratchBoundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string scratch = Path.GetFullPath(Path.Combine(scratchBoundary, "KillerShell-script-lifetime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(scratch);
        Exception? fixtureFailure = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = host,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + fixture +
                    "\" -ModulePath \"" + module + "\" -ScratchRoot \"" + scratch + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.EnvironmentVariables["TEMP"] = scratch;
            start.EnvironmentVariables["TMP"] = scratch;
            start.EnvironmentVariables.Remove("PSModulePath");
            using var process = Process.Start(start);
            Assert.NotNull(process);
            Task<string> stdout = process!.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(120000);
            if (!exited) { process.Kill(); process.WaitForExit(); }
            Assert.True(exited, host + " detached script fixture timed out.");
            Assert.True(process.ExitCode == 0, host + Environment.NewLine + await stdout + Environment.NewLine + await stderr);
        }
        catch (Exception error)
        {
            fixtureFailure = error;
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    string cleanupTarget = new DirectoryInfo(scratch).FullName;
                    Assert.True(string.Equals(cleanupTarget, scratch, StringComparison.OrdinalIgnoreCase)
                        && cleanupTarget.StartsWith(scratchBoundary, StringComparison.OrdinalIgnoreCase),
                        "The cleanup target must remain inside the owned scratch boundary.");
                    Directory.Delete(cleanupTarget, true);
                }
            }
            catch (Exception cleanupFailure) when (fixtureFailure != null)
            {
                throw new AggregateException("The fixture failed and its scratch cleanup also failed.", fixtureFailure, cleanupFailure);
            }
        }
    }
}
