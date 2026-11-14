using System;
using System.Diagnostics;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// The recents menu checks its folders before it opens. A dead share must not freeze the window
// until SMB gives up: every folder is checked at once, and one that does not answer in time is
// left out of this opening.
public sealed class ReachabilityTests
{
    [Fact]
    public void AHungProbeDoesNotHoldTheOthers()
    {
        using var release = new ManualResetEventSlim();
        var clock = Stopwatch.StartNew();
        var live = Reachability.Reachable(new[] { @"C:\a", @"\\dead\share", @"C:\b" },
            path => { if (path.StartsWith(@"\\")) release.Wait(TimeSpan.FromSeconds(10)); return true; },
            TimeSpan.FromMilliseconds(500));
        clock.Stop();
        release.Set();

        Assert.Equal(new[] { @"C:\a", @"C:\b" }, live);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "took " + clock.Elapsed);
    }

    // A dead share that is still being checked from the last time is not checked again on top
    // of it, so opening the favorites drawer repeatedly does not pile up stuck threads.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProbeStillRunningIsReusedNotRepeated(bool isDirectory)
    {
        using var release = new ManualResetEventSlim();
        int calls = 0;
        string path = @"\\dead-" + Guid.NewGuid().ToString("N") + @"\share";
        bool Probe(string p) { Interlocked.Increment(ref calls); release.Wait(TimeSpan.FromSeconds(10)); return true; }

        Assert.Empty(Reachability.Reachable(new[] { path }, Probe, TimeSpan.FromMilliseconds(100), isDirectory));
        Assert.Empty(Reachability.Reachable(new[] { path }, Probe, TimeSpan.FromMilliseconds(100), isDirectory));
        release.Set();

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void ABlockedDirectoryProbeCannotAnswerAFileProbeForTheSamePath()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        string path = @"\\kind-" + Guid.NewGuid().ToString("N") + @"\share";
        int fileCalls = 0;
        try
        {
            Assert.Empty(Reachability.Reachable(new[] { path }, _ =>
            {
                started.Set();
                try { release.Wait(TimeSpan.FromSeconds(10)); return false; }
                finally { finished.Set(); }
            }, TimeSpan.FromMilliseconds(100)));
            Assert.True(started.Wait(1000), "The directory probe did not start.");

            bool FileProbe(string _) { Interlocked.Increment(ref fileCalls); return true; }
            Assert.Equal(new[] { path }, Reachability.Reachable(new[] { path }, FileProbe,
                TimeSpan.FromSeconds(2), isDirectory: false));
            Assert.Equal(1, Volatile.Read(ref fileCalls));
            Assert.False(finished.IsSet, "The directory probe must remain blocked while the file probe answers.");
        }
        finally
        {
            release.Set();
            if (started.IsSet) finished.Wait(5000);
        }
    }

    [Fact]
    public void OrderIsKeptAndMissingFoldersAreDropped()
        => Assert.Equal(new[] { "1", "3" },
            Reachability.Reachable(new[] { "1", "2", "3" }, path => path != "2", TimeSpan.FromSeconds(2)));

    [Fact]
    public void AProbeThatThrowsCountsAsUnreachable()
        => Assert.Empty(Reachability.Reachable(new[] { "x" }, _ => throw new UnauthorizedAccessException(), TimeSpan.FromSeconds(2)));
}
