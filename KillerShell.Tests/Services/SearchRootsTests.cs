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
