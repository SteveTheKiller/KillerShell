using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace KillerShell.Tests.Modules;

public sealed class ModuleSafetyTests
{
    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task EmbeddedModulesPassPublicCommandFixtures(string host)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string repo = directory!.FullName;
        string fixtureRoot = Path.Combine(repo, "KillerShell.Tests", "Modules");
        string scratch = Path.Combine(Path.GetTempPath(), "ks-mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = host,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(fixtureRoot, "Test-Modules.ps1") +
                    "\" -ModuleRoot \"" + Path.Combine(repo, "Modules") + "\" -FixtureRoot \"" + fixtureRoot +
                    "\" -ScratchRoot \"" + scratch + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.EnvironmentVariables["TEMP"] = scratch;
            start.EnvironmentVariables["TMP"] = scratch;
            // Each PowerShell edition supplies its own built-in modules.
            start.EnvironmentVariables.Remove("PSModulePath");
            using var process = Process.Start(start);
            Assert.NotNull(process);
            Task<string> stdout = process!.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(240000);
            if (!exited) { process.Kill(); process.WaitForExit(); }
            Assert.True(exited, host + " module validation timed out.");
            Assert.True(process.ExitCode == 0, host + Environment.NewLine + await stdout + Environment.NewLine + await stderr);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }
}
