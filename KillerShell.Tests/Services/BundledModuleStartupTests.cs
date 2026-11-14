using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KillerShell.Shell;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Services;

[CollectionDefinition("Bundled module startup", DisableParallelization = true)]
public sealed class BundledModuleStartupCollection { }

[Collection("Bundled module startup")]
public sealed class BundledModuleStartupTests
{
    [PowerShell7Fact]
    public async Task PowerShellTabsResolveBundledCommandsWithAndWithoutThePrompt()
    {
        string userModules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PowerShell", "Modules");
        string competitor = Path.Combine(userModules, "KillerScripts", "1.0.1");
        Assert.False(Directory.Exists(competitor), "The fixture will not replace an installed KillerScripts 1.0.1.");

        string? oldPath = Environment.GetEnvironmentVariable("PSModulePath");
        string? oldPrompt = Environment.GetEnvironmentVariable("KS_PROMPT");
        bool ownsCompetitor = false;
        try
        {
            Directory.CreateDirectory(competitor);
            ownsCompetitor = true;
            File.WriteAllText(Path.Combine(competitor, "KillerScripts.psm1"),
                "function amort { 'gallery stub' }; Export-ModuleMember -Function amort", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(competitor, "KillerScripts.psd1"),
                "@{ RootModule = 'KillerScripts.psm1'; ModuleVersion = '1.0.1'; " +
                "GUID = 'f9c368f1-4998-4cc6-b1f6-3af400f6534a'; FunctionsToExport = @('amort') }",
                new UTF8Encoding(false));

            MainWindow.EnsureBundledModules();
            string appPath = Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty;
            string bundledRoot = appPath.Split(';')[0];
            Assert.True(File.Exists(Path.Combine(bundledRoot, "KillerScripts", "KillerScripts.psd1")),
                "KillerShell must unpack its bundled module before the shell starts.");

            var results = new List<(string Prompt, string ModuleBase)>();
            foreach (string prompt in new[] { "1", "0" })
            {
                Environment.SetEnvironmentVariable("KS_PROMPT", prompt);
                var profile = TerminalProfile.PowerShell();
                Assert.Equal("pwsh.exe", Path.GetFileName(profile.ExePath), ignoreCase: true);
                string quotedExe = "\"" + profile.ExePath + "\"";
                Assert.StartsWith(quotedExe, profile.CommandLine);
                string arguments = profile.CommandLine.Substring(quotedExe.Length);
                const string observe = "try { [Console]::Out.WriteLine('KS_MODULE_BASE=' + " +
                    "(Get-Command amort -ErrorAction Stop).Module.ModuleBase); exit 0 } " +
                    "catch { [Console]::Error.WriteLine($_); exit 1 }";
                // Keep the generated startup prefix intact. Only append the observation and an
                // explicit exit so the app's interactive -NoExit session can finish unattended.
                arguments = arguments.IndexOf("-Command ", StringComparison.OrdinalIgnoreCase) >= 0
                    ? arguments.Substring(0, arguments.Length - 1) + "; " + observe + "\""
                    : arguments + " -Command \"" + observe + "\"";

                var start = new ProcessStartInfo(profile.ExePath, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.EnvironmentVariables["PSModulePath"] = appPath;
                using var process = Process.Start(start)!;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                bool exited = process.WaitForExit(30000);
                if (!exited) { process.Kill(); process.WaitForExit(); }
                string output = await stdout;
                string errors = await stderr;
                Assert.True(exited, "PowerShell module startup timed out with KS_PROMPT=" + prompt + ".");
                Assert.True(process.ExitCode == 0, "KS_PROMPT=" + prompt + Environment.NewLine + output + Environment.NewLine + errors);
                string line = Assert.Single(output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(value => value.StartsWith("KS_MODULE_BASE=", StringComparison.Ordinal)));
                results.Add((prompt, line.Substring("KS_MODULE_BASE=".Length)));
            }

            string expected = Path.GetFullPath(Path.Combine(bundledRoot, "KillerScripts")).TrimEnd('\\');
            foreach (var result in results)
                Assert.True(string.Equals(expected, Path.GetFullPath(result.ModuleBase).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                    "KS_PROMPT=" + result.Prompt + " resolved amort from " + result.ModuleBase + ", expected " + expected + ".");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", oldPath);
            Environment.SetEnvironmentVariable("KS_PROMPT", oldPrompt);
            if (ownsCompetitor) Directory.Delete(competitor, true);
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PowerShell7FactAttribute : FactAttribute
{
    public PowerShell7FactAttribute()
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';').ToList();
        foreach (string variable in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(root)) continue;
            folders.Add(Path.Combine(root!, "PowerShell", "7"));
            folders.Add(Path.Combine(root!, "PowerShell", "7-preview"));
        }
        if (!folders.Any(folder => !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder.Trim(), "pwsh.exe"))))
            Skip = "Needs PowerShell 7 to verify its module resolution during terminal startup.";
    }
}
