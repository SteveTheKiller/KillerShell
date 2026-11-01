using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateTaskSweepTests
{
    [Fact]
    public void FinishedUpdateTasksAreDeletedAndQueuedRunningAndForeignTasksStay()
    {
        var deleted = new List<string>();
        var scheduler = new UpdateTaskSweep.Scheduler
        {
            Tasks = () => new[]
            {
                ("KillerUpdate-aaaa", 3), ("KillerUpdate-bbbb", 1), ("KillerUpdate-cccc", 2),
                ("KillerUpdate-dddd", 4), ("killerupdate-eeee", 3), ("OtherVendorTask", 3), ("NotKillerUpdate-ffff", 3),
            },
            Delete = deleted.Add,
        };
        Assert.Equal(3, UpdateTaskSweep.Run(scheduler));
        Assert.Equal(new[] { "KillerUpdate-aaaa", "KillerUpdate-bbbb", "killerupdate-eeee" }, deleted);
    }

    [Fact]
    public void AnUnavailableSchedulerOrRefusedDeleteNeverThrows()
    {
        Assert.Equal(0, UpdateTaskSweep.Run(new UpdateTaskSweep.Scheduler { Tasks = () => throw new System.InvalidOperationException() }));
        var scheduler = new UpdateTaskSweep.Scheduler
        {
            Tasks = () => new[] { ("KillerUpdate-aaaa", 3), ("KillerUpdate-bbbb", 3) },
            Delete = name => { if (name.EndsWith("aaaa")) throw new System.UnauthorizedAccessException(); },
        };
        Assert.Equal(1, UpdateTaskSweep.Run(scheduler));
    }
}
