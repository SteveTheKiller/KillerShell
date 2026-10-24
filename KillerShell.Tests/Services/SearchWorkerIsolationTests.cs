using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class SearchWorkerIsolationTests
{
    [Fact]
    public Task SearchCompletesWhenOnlyItsCoordinatorHasAPoolThread()
        => RuntimeComponentProcess.Run(nameof(SearchWorkerIsolationScenario));
}

public static class SearchWorkerIsolationScenario
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-pool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "probe.txt"), "search fixture");
        ThreadPool.GetMinThreads(out int oldMin, out int oldIoMin);
        ThreadPool.GetMaxThreads(out int oldMax, out int oldIoMax);
        int limit = Math.Max(2, Environment.ProcessorCount);
        using var release = new ManualResetEventSlim();
        using var started = new CountdownEvent(limit - 1);
        using var finished = new CountdownEvent(limit - 1);
        using var cancellation = new CancellationTokenSource();
        Task? search = null;
        bool completed = false;
        var found = new List<SearchResult>();
        try
        {
            Assert.True(ThreadPool.SetMinThreads(1, oldIoMin));
            Assert.True(ThreadPool.SetMaxThreads(limit, oldIoMax));
            Assert.True(ThreadPool.SetMinThreads(limit, oldIoMin));
            for (int index = 0; index < limit - 1; index++)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { started.Signal(); release.Wait(); }
                    finally { finished.Signal(); }
                });
            Assert.True(started.Wait(5000), "The bounded pool did not reach its controlled load.");
            var group = new TermGroup();
            group.Terms.Add(new SearchTerm { Pattern = "probe", Mode = SearchTerm.SearchMode.FileName });
            var engine = new SearchEngine();
            engine.ResultsBatch += batch => found.AddRange(batch);
            search = engine.SearchAsync(root, new[] { group }, Array.Empty<SearchFilter>(), "*.*", "", false, cancellation.Token);
            completed = search.Wait(3000);
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(oldMax, oldIoMax);
            ThreadPool.SetMinThreads(oldMin, oldIoMin);
            Assert.True(finished.Wait(5000), "The controlled pool load did not stop.");
            cancellation.Cancel();
            try { search?.Wait(5000); } catch (AggregateException) { }
            string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string cleanup = Path.GetFullPath(root);
            Assert.StartsWith(boundary, cleanup, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(cleanup, true);
        }
        Assert.True(completed, "Search starved while its producer and consumers waited for the coordinator's pool thread.");
        Assert.Equal("probe.txt", Assert.Single(found).FileName);
    }
}
