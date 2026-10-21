using System;
using System.Diagnostics;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class CliCommandTests
{
    [Fact]
    public void SearchReportsASkippedJunctionRoot()
    {
        using var fixture = new Services.SearchLinkFixture();
        fixture.AddLink();

        Result result = Run("search", fixture.Link, "--name", "*.txt");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"results\":[]", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"limitReached\":false", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"skippedDirectoryLinks\":1", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"incompletePaths\":0", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchKeepsResultsAndReportsSkippedDescendantLinks(bool addLink)
    {
        using var fixture = new Services.SearchLinkFixture();
        if (addLink) fixture.AddLink();

        Result result = Run("search", fixture.Root, "--name", "*.txt");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fixture.txt", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"limitReached\":false", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"skippedDirectoryLinks\":" + (addLink ? "1" : "0"), result.Output, StringComparison.Ordinal);
        Assert.Contains("\"incompletePaths\":0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchReportsUnreadableContentWhileKeepingReadableResults()
    {
        using var fixture = new Services.SearchLinkFixture();
        string blocked = Path.Combine(fixture.Root, "blocked.txt");
        File.WriteAllText(blocked, "fixture");
        using var locked = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None);

        Result result = Run("search", fixture.Root, "--content", "fixture");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fixture.txt", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked.txt", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"limitReached\":false", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"skippedDirectoryLinks\":0", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"incompletePaths\":1", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyFileCommandsReturnBoundedJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Cli.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "notes.txt");
            File.WriteAllText(file, "alpha beta gamma");
            Directory.CreateDirectory(Path.Combine(root, "Folder"));

            Result listing = Run("list", root, "--limit", "1");
            Assert.Equal(0, listing.ExitCode);
            Assert.Contains("\"entries\":[", listing.Output, StringComparison.Ordinal);
            Assert.Contains("\"limitReached\":true", listing.Output, StringComparison.Ordinal);

            Result details = Run("info", file);
            Assert.Equal(0, details.ExitCode);
            Assert.Contains("\"name\":\"notes.txt\"", details.Output, StringComparison.Ordinal);
            Assert.Contains("\"isDirectory\":false", details.Output, StringComparison.Ordinal);

            Result read = Run("read", file, "--max-chars", "5");
            Assert.Equal(0, read.ExitCode);
            Assert.Contains("\"text\":\"alpha\"", read.Output, StringComparison.Ordinal);
            Assert.Contains("\"truncated\":true", read.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReadNeverSplitsASurrogatePair()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Cli.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "emoji.txt");
            File.WriteAllText(file, "ab\U0001F600cd");

            Result read = Run("read", file, "--max-chars", "3");

            Assert.Equal(0, read.ExitCode);
            Assert.Contains("\"text\":\"ab\"", read.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(((char)0xFFFD).ToString(), read.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExplicitJsonKeepsTheExistingOutput()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "JSON compatibility");
            Result original = Run("info", file);
            Result explicitJson = Run("info", file, "--json");
            Assert.Equal(0, original.ExitCode);
            Assert.True(explicitJson.ExitCode == 0, explicitJson.Error);
            Assert.Equal(original.Output, explicitJson.Output);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void SystemInspectionCommandsReturnBoundedJson()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "KillerShell MCP");
            Result hash = Run("hash", file);
            Assert.Equal(0, hash.ExitCode);
            Assert.Contains("\"algorithm\":\"SHA-256\"", hash.Output, StringComparison.Ordinal);

            Result processes = Run("processes", "--limit", "1");
            Assert.Equal(0, processes.ExitCode);
            Assert.Contains("\"processes\":[", processes.Output, StringComparison.Ordinal);
            Assert.Contains("\"limitReached\":true", processes.Output, StringComparison.Ordinal);

            Result drives = Run("drives");
            Assert.Equal(0, drives.ExitCode);
            Assert.Contains("\"drives\":[", drives.Output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft")]
    [InlineData(@"HKCU\Software\Microsoft")]
    [InlineData(@"hkcu\Software\Microsoft")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft")]
    public void RegistryAcceptsFullAndShortHiveNames(string path)
    {
        Result key = Run("registry", path, "--limit", "1");
        Assert.Equal(0, key.ExitCode);
        Assert.Contains("\"subkeys\":[", key.Output, StringComparison.Ordinal);
    }

    private static Result Run(params string[] arguments)
    {
        string executable = typeof(SearchEngine).Assembly.Location;
        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--cli " + string.Join(" ", Array.ConvertAll(arguments, Quote)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("KillerShell did not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000), "KillerShell CLI timed out.");
        return new Result(process.ExitCode, output, error);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private sealed class Result
    {
        internal Result(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output;
            Error = error;
        }

        internal int ExitCode { get; }
        internal string Output { get; }
        internal string Error { get; }
    }
}
