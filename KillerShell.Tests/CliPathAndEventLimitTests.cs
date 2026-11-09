using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class CliPathAndEventLimitTests
{
    [Theory]
    [InlineData(@"C:\folder:stream")]
    [InlineData(@"C:\a|b")]
    public void APathWindowsCannotParseIsABadArgumentNotAFailure(string path)
    {
        foreach (string verb in new[] { "list", "info", "read", "hash" })
        {
            var result = Run(verb, path);
            Assert.True(result.ExitCode == 2, verb + " exited " + result.ExitCode + ": " + result.Error);
        }
    }

    [Fact]
    public void APathPastTheLengthLimitIsABadArgumentNotAFailure()
    {
        string path = @"C:\" + new string('a', 300) + @"\" + new string('b', 300);
        var result = Run("list", path);
        Assert.True(result.ExitCode == 2, "exited " + result.ExitCode + ": " + result.Error);
    }

    // A search that finds exactly as many matches as its limit found them all, so it does not
    // claim more were left behind. One fewer than the matches does.
    [Fact]
    public void SearchReportsTheLimitOnlyWhenMoreMatchesExist()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            for (int i = 0; i < 3; i++) System.IO.File.WriteAllText(System.IO.Path.Combine(root, "match" + i + ".txt"), "x");

            var exact = Run("search", root, "--name", "match*.txt", "--limit", "3");
            Assert.Equal(0, exact.ExitCode);
            Assert.Contains("\"limitReached\":false", exact.Output, StringComparison.Ordinal);
            Assert.Equal(3, CountOf(exact.Output, "\"path\":"));

            var short1 = Run("search", root, "--name", "match*.txt", "--limit", "2");
            Assert.Contains("\"limitReached\":true", short1.Output, StringComparison.Ordinal);
            Assert.Equal(2, CountOf(short1.Output, "\"path\":"));
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // A level whose newest events number between 1 and 100, so a limit can be set to exactly that
    // count. Null when this machine's System log has no such level.
    private static (int Level, int Count)? SmallLevel()
    {
        foreach (int level in new[] { 1, 2, 3, 4 })
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, EventLevelQuery.For(level)));
            int count = 0;
            while (count <= 100)
            {
                using EventRecord? record = reader.ReadEvent();
                if (record == null) break;
                count++;
            }
            if (count >= 1 && count <= 100) return (level, count);
        }
        return null;
    }

    [Fact]
    public void EventsReportTheLimitOnlyWhenMoreMatchesExist()
    {
        var small = SmallLevel();
        if (small == null) return;
        string name = new[] { "", "Critical", "Error", "Warning", "Information" }[small.Value.Level];
        int count = small.Value.Count;

        var exact = Run("events", "System", "--level", name, "--limit", count.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(0, exact.ExitCode);
        Assert.Contains("\"limitReached\":false", exact.Output, StringComparison.Ordinal);
        Assert.Contains("\"scanLimitReached\":false", exact.Output, StringComparison.Ordinal);

        var roomy = Run("events", "System", "--level", name, "--limit", Math.Min(100, count + 1).ToString(CultureInfo.InvariantCulture));
        Assert.Contains("\"limitReached\":false", roomy.Output, StringComparison.Ordinal);

        if (count >= 2)
        {
            var short1 = Run("events", "System", "--level", name, "--limit", (count - 1).ToString(CultureInfo.InvariantCulture));
            Assert.Contains("\"limitReached\":true", short1.Output, StringComparison.Ordinal);
        }
    }

    private static (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = typeof(SearchEngine).Assembly.Location,
            Arguments = "--cli " + string.Join(" ", Array.ConvertAll(arguments, a => "\"" + a.Replace("\"", "\\\"") + "\"")),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("KillerShell did not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), "KillerShell CLI timed out.");
        return (process.ExitCode, output, error);
    }
}
