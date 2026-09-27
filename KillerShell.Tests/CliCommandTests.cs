using System;
using System.Diagnostics;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class CliCommandTests
{
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
