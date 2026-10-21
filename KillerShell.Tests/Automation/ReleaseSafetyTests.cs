using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace KillerShell.Tests.ReleaseFixtures;

public sealed class ReleaseSafetyTests
{
    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task ReleaseBoundaryFixturesPassOnBothPowerShellHosts(string host)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string repo = directory!.FullName;
        string scratch = Path.Combine(Path.GetTempPath(), "KillerShell-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (string fixture in new[] { "ReleaseAudit", "ReleaseGit", "ReleaseSafety", "ChocolateySubmission", "ChocolateyValidation" })
            {
                var start = new ProcessStartInfo
                {
                    FileName = host,
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" +
                        Path.Combine(repo, "KillerShell.Tests", "Automation", "Test-" + fixture + ".ps1") + "\"" +
                        (fixture == "ChocolateyValidation" ? " -ScratchRoot \"" + Path.Combine(scratch, fixture) + "\"" : ""),
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.EnvironmentVariables["TEMP"] = scratch;
                start.EnvironmentVariables["TMP"] = scratch;
                start.EnvironmentVariables.Remove("PSModulePath");
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                bool exited = process.WaitForExit(120000);
                if (!exited) { process.Kill(); process.WaitForExit(); }
                Assert.True(exited, host + " " + fixture + " exceeded its deadline.");
                Assert.True(process.ExitCode == 0, host + " " + fixture + Environment.NewLine + await output + await errors);
            }
        }
        finally { Directory.Delete(scratch, true); }
    }
}
