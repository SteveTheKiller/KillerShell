using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class SearchTraversalTests
{
    [Fact]
    public async Task PreCanceledSearchDoesNotEnumerateEvenAnEmptyRoot()
    {
        int calls = 0;
        var engine = new SearchEngine(_ => Stream.Null,
            _ => { calls++; return Array.Empty<string>(); },
            _ => { calls++; return Array.Empty<string>(); });
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await engine.SearchAsync("root", [], [], "", "", false, stop.Token);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationAfterEmptyDirectoryEnumerationStopsBeforeNextNativeCall()
    {
        using var stop = new CancellationTokenSource();
        int directories = 0;
        var engine = new SearchEngine(_ => Stream.Null,
            _ => { stop.Cancel(); return Array.Empty<string>(); },
            _ => { directories++; return Array.Empty<string>(); });
        await engine.SearchAsync("root", [], [], "", "", false, stop.Token);
        Assert.Equal(0, directories);
    }

    [Fact]
    public async Task CancellationBetweenDirectoryEntriesDisposesTheEnumerator()
    {
        using var stop = new CancellationTokenSource();
        bool disposed = false;
        int returned = 0;
        IEnumerable<string> Directories(string root)
        {
            if (root != "root") yield break;
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    returned++;
                    stop.Cancel();
                    yield return root + i;
                }
            }
            finally { disposed = true; }
        }
        var engine = new SearchEngine(_ => Stream.Null, _ => Array.Empty<string>(), Directories);
        await engine.SearchAsync("root", [], [], "", "", false, stop.Token);
        Assert.Equal(1, returned);
        Assert.True(disposed);
    }
}
