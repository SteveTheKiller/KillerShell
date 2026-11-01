using System;
using System.Diagnostics;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class CliCommandTests
{
    [Fact]
    public void SearchWalksAJunctionRootAndSkipsTheLinkInsideIt()
    {
        using var fixture = new Services.SearchLinkFixture();
        fixture.AddLink();

        Result result = Run("search", fixture.Link, "--name", "*.txt");

        // The folder asked for is searched even though it is a junction; the junction nested
        // inside it, which points back up, is skipped so the walk cannot loop.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fixture.txt", result.Output, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("/version")]
    public void VersionSwitchesPrintTheAssemblyVersion(string flag)
    {
        Result result = Run(flag);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(typeof(SearchEngine).Assembly.GetName().Version + Environment.NewLine, result.Output);
    }

    [Fact]
    public void UsageNamesVersionExitCodesAndEveryVerb()
    {
        Result result = Run("--help");
        Assert.Equal(0, result.ExitCode);
        foreach (string text in new[] { "--version", "Exit codes", "0 ", "1 ", "2 ", "storage <", "--detail", "--filter", "--level", "--csv", "--html" })
            Assert.Contains(text, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessesDetailAndFilterNarrowTheList()
    {
        Result result = Run("processes", "--filter", "killershell", "--detail");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"commandLine\":", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"parentPid\":", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"explorer\"", result.Output, StringComparison.Ordinal);

        Result none = Run("processes", "--filter", "no-such-process-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("\"processes\":[]", none.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicesDetailAndFilterNarrowTheList()
    {
        Result result = Run("services", "--filter", "RpcSs", "--detail", "--limit", "5");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"name\":\"RpcSs\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"startMode\":", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"logOnAs\":", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"Spooler\"", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void EventsLevelAndFilterNarrowTheList()
    {
        Result errors = Run("events", "System", "--level", "Error", "--limit", "3");
        Assert.Equal(0, errors.ExitCode);
        Assert.DoesNotContain("\"level\":\"Information\"", errors.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"level\":\"Warning\"", errors.Output, StringComparison.Ordinal);

        Result badLevel = Run("events", "System", "--level", "Loud");
        Assert.Equal(2, badLevel.ExitCode);

        Result filtered = Run("events", "System", "--filter", "no-such-text-" + Guid.NewGuid().ToString("N"), "--limit", "3");
        Assert.Equal(0, filtered.ExitCode);
        Assert.Contains("\"events\":[]", filtered.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchWritesCsvAndHtmlToNewPathsAndRefusesExistingOnes()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Cli.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "alpha.txt"), "needle");
            string csv = Path.Combine(root, "out.csv");
            string html = Path.Combine(root, "out.html");

            Result first = Run("search", root, "--name", "alpha*", "--csv", csv, "--html", html);
            Assert.True(first.ExitCode == 0, first.Error);
            Assert.Contains("alpha.txt", first.Output, StringComparison.Ordinal);
            Assert.StartsWith("Name,Folder,SizeBytes", File.ReadAllText(csv).TrimStart('﻿'), StringComparison.Ordinal);
            Assert.Contains("alpha.txt", File.ReadAllText(html), StringComparison.Ordinal);

            string before = File.ReadAllText(csv);
            Result again = Run("search", root, "--name", "alpha*", "--csv", csv);
            Assert.Equal(2, again.ExitCode);
            Assert.Equal(before, File.ReadAllText(csv));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StorageReportsTopEntriesBySizeAndWritesHtmlOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Cli.Tests-" + Guid.NewGuid().ToString("N"));
        string scan = Path.Combine(root, "scan");
        Directory.CreateDirectory(Path.Combine(scan, "big"));
        Directory.CreateDirectory(Path.Combine(scan, "small"));
        try
        {
            File.WriteAllBytes(Path.Combine(scan, "big", "huge.bin"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(scan, "small", "tiny.bin"), new byte[10]);
            string html = Path.Combine(root, "storage.html");

            Result result = Run("storage", scan, "--limit", "2", "--html", html);
            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Contains("\"totalBytes\":5010", result.Output, StringComparison.Ordinal);
            Assert.Contains("\"files\":2", result.Output, StringComparison.Ordinal);
            Assert.True(result.Output.IndexOf("huge.bin", StringComparison.Ordinal) < result.Output.IndexOf("tiny.bin", StringComparison.Ordinal));
            Assert.Contains("huge.bin", File.ReadAllText(html), StringComparison.Ordinal);

            Result limited = Run("storage", scan, "--limit", "1");
            Assert.Equal(0, limited.ExitCode);
            Assert.DoesNotContain("tiny.bin", limited.Output, StringComparison.Ordinal);
            Assert.Contains("\"limitReached\":true", limited.Output, StringComparison.Ordinal);

            Assert.Equal(2, Run("storage", scan, "--html", html).ExitCode);
            Assert.Equal(2, Run("storage", Path.Combine(root, "missing")).ExitCode);
            Assert.Equal(2, Run("storage").ExitCode);
        }
        finally { Directory.Delete(root, true); }
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
