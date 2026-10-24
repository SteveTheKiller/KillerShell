using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class SearchRootsTests
{
    [Fact]
    public async Task AHangingRootIsLeftOutWithinTheTimeLimit()
    {
        using var hang = new ManualResetEventSlim(false);
        try
        {
            var candidates = new[] { @"C:\Fast", @"Z:\Dead", @"C:\Missing", @"D:\Also" };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var found = await SearchRoots.ProbeAsync(candidates, path =>
            {
                if (path.StartsWith("Z:")) { hang.Wait(); return true; }
                return path != @"C:\Missing";
            }, TimeSpan.FromMilliseconds(300));
            Assert.True(watch.ElapsedMilliseconds < 3000, "The probe waited on the hanging root: " + watch.ElapsedMilliseconds + " ms");
            Assert.Equal(new[] { @"C:\Fast", @"D:\Also" }, found);
        }
        finally { hang.Set(); }
    }

    [Fact]
    public async Task AProbeThatThrowsCountsAsMissing()
    {
        var found = await SearchRoots.ProbeAsync(new[] { @"C:\A", @"C:\B" },
            path => path == @"C:\A" ? throw new IOException("network name no longer available") : true,
            TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { @"C:\B" }, found);
    }

    [Fact]
    public void NormalizationRemovesOverlappingRootsWithoutMatchingSiblingNames()
    {
        var roots = SearchRoots.Normalize(new[] { @"C:\Docs\Work", @"c:\docs", @"C:\Docs2", @"C:\DOCS\" });
        Assert.Equal(new[] { @"c:\docs", @"C:\Docs2" }, roots);
        Assert.Equal(new[] { @"C:\" }, SearchRoots.Normalize(new[] { @"C:\Docs", @"C:\" }));
    }

    [Fact]
    public async Task SearchVisitsEveryRootAndDoesNotDuplicateNestedMatches()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
        string first = Path.Combine(sandbox, "first");
        string nested = Path.Combine(first, "nested");
        string second = Path.Combine(sandbox, "second");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(first, "one.txt"), "sample");
            File.WriteAllText(Path.Combine(nested, "two.txt"), "sample");
            File.WriteAllText(Path.Combine(second, "three.txt"), "sample");
            var group = new TermGroup();
            group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.FileName, Pattern = "*.txt" });
            var results = new List<SearchResult>();
            var engine = new SearchEngine();
            engine.ResultsBatch += results.AddRange;
            await engine.SearchAsync(first, new[] { group }, new SearchFilter[0], "*", "", false,
                CancellationToken.None, rootPaths: new[] { first, nested, second });
            Assert.Equal(3, results.Count);
            Assert.Equal(0, engine.IncompletePaths);
        }
        finally { Directory.Delete(sandbox, true); }
    }
}
