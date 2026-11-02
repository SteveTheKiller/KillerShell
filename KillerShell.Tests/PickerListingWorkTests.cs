using System;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Controls;
using Xunit;

namespace KillerShell.Tests;

public sealed class PickerListingWorkTests
{
    // The deadline is for the read. A listing that arrived in time must be shown even when putting
    // its rows on screen takes longer than the deadline.
    [Fact]
    public async Task ASlowPublishAfterAFinishedReadIsNotTimedOut()
    {
        using var work = new PickerListingWork { RequestTimeout = TimeSpan.FromSeconds(2) };
        bool published = false;
        Exception? failure = null;

        await work.RunAsync(_ => 7,
            async (rows, token) =>
            {
                await Task.Delay(3000);
                token.ThrowIfCancellationRequested();
                published = true;
            },
            error => failure = error);

        Assert.Null(failure);
        Assert.True(published);
    }

    [Fact]
    public async Task AReadThatOutlastsTheDeadlineStillTimesOut()
    {
        using var work = new PickerListingWork { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        Exception? failure = null;
        using var release = new ManualResetEventSlim(false);

        await work.RunAsync(_ => { release.Wait(5000); return 1; },
            (rows, token) => Task.CompletedTask,
            error => failure = error);
        release.Set();

        Assert.IsType<TimeoutException>(failure);
    }
}
