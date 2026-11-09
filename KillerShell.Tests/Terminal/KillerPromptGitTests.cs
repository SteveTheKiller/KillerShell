using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace KillerShell.Tests.Terminal;

// The terminal prompt shows the git branch inside a repository. A folder the prompt already
// visited before `git init` must show the branch once it becomes a repository, not stay
// remembered as "not a repo" for the rest of the session.
public sealed class KillerPromptGitTests
{
    [Fact]
    public async System.Threading.Tasks.Task AFolderThatBecomesARepositoryIsSeenAsOne()
    {
        string repo = FindRepoRoot();
        string prompt = Path.Combine(repo, "Terminal", "KillerPrompt.ps1");
        string folder = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string script = ". '" + prompt.Replace("'", "''") + "'; Set-Location -LiteralPath '" + folder.Replace("'", "''") + "'; " +
                "$before = KFInRepo; New-Item -ItemType Directory -Path .git | Out-Null; $after = KFInRepo; " +
                "[Console]::Out.Write(\"$before $after\")";
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(30000), "The prompt fixture timed out.");
            Assert.True(p.ExitCode == 0, await errors);
            Assert.Equal("False True", (await output).Trim());
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerShell repository root not found.");
    }
}
