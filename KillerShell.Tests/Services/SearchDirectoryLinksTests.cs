using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Tests.Editing;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class SearchDirectoryLinksTests
{
    [Fact]
    public Task AncestorJunctionDoesNotRepeatThePhysicalSubtree()
        => EditorScenarioProcess.Run(typeof(SearchDirectoryLinksScenario).FullName!);

    [Fact]
    public async Task FailedEnumeratorReportsPartialResultsAndContinuesSiblings()
    {
        using var fixture = new SearchLinkFixture();
        string child = Path.Combine(fixture.Root, "child");
        Directory.CreateDirectory(child);
        string second = Path.Combine(child, "second.txt");
        File.WriteAllText(second, "fixture");
        IEnumerable<string> Files(string path)
        {
            if (path == fixture.Root)
            {
                yield return fixture.File;
                throw new IOException("Enumeration failed after the first item.");
            }
            yield return second;
        }
        var engine = new SearchEngine(_ => Stream.Null, Files, Directory.EnumerateDirectories);
        var results = await Search(engine, fixture.Root);
        Assert.Equal(2, results.Count);
        Assert.Equal(1, engine.IncompletePaths);
    }

    internal static async Task<List<SearchResult>> Search(SearchEngine engine, string root)
    {
        var group = new TermGroup();
        group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.FileName, Pattern = "*.txt" });
        var found = new List<SearchResult>();
        engine.ResultsBatch += batch => found.AddRange(batch);
        await engine.SearchAsync(root, [group], [], "", "", false, CancellationToken.None);
        return found;
    }
}

public static class SearchDirectoryLinksScenario
{
    public static void Run()
    {
        using var fixture = new SearchLinkFixture();
        fixture.AddLink();
        var engine = new SearchEngine();
        var results = SearchDirectoryLinksTests.Search(engine, fixture.Root).GetAwaiter().GetResult();
        Assert.Equal(fixture.File, Assert.Single(results).FilePath);
        Assert.Equal(1, engine.SkippedDirectoryLinks);
        Assert.Equal("fixture", File.ReadAllText(fixture.File));
        // Searching the junction itself walks it, since the user chose that folder; the link
        // nested inside it is still skipped, so the walk cannot loop.
        var throughLink = SearchDirectoryLinksTests.Search(engine, fixture.Link).GetAwaiter().GetResult();
        Assert.Equal(Path.Combine(fixture.Link, "fixture.txt"), Assert.Single(throughLink).FilePath);
        Assert.Equal(1, engine.SkippedDirectoryLinks);
    }
}

internal sealed class SearchLinkFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ks-search-links-" + Guid.NewGuid().ToString("N"));
    internal string File => Path.Combine(Root, "fixture.txt");
    internal string Link => Path.Combine(Root, "ancestor");
    internal SearchLinkFixture()
    {
        Directory.CreateDirectory(Root);
        System.IO.File.WriteAllText(File, "fixture");
    }
    internal void AddLink()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + Link + "\" \"" + Root + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        Assert.True(process.WaitForExit(5000), "Owned junction creation timed out.");
        Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
    }
    public void Dispose()
    {
        string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(Link)) Directory.Delete(Link);
        Directory.Delete(Root, true);
    }
}
