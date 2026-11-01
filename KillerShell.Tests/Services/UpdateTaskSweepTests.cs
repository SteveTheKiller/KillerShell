using System;
using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateTaskSweepTests
{
    private static readonly DateTime Now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddHours(-1);

    [Fact]
    public void ATaskRegisteredMinutesAgoStaysBecauseItsHelperHasNotStartedItYet()
    {
        var deleted = new List<string>();
        var scheduler = new UpdateTaskSweep.Scheduler
        {
            Tasks = () => new[] { ("KillerUpdate-young", 3, Now.AddSeconds(-30)), ("KillerUpdate-edge", 3, Now.AddMinutes(-2)), ("KillerUpdate-old", 3, Now.AddMinutes(-10)) },
            Delete = deleted.Add,
            Now = () => Now,
        };
        Assert.Equal(1, UpdateTaskSweep.Run(scheduler));
        Assert.Equal(new[] { "KillerUpdate-old" }, deleted);
    }

    [Fact]
    public void FinishedUpdateTasksAreDeletedAndQueuedRunningAndForeignTasksStay()
    {
        var deleted = new List<string>();
        var scheduler = new UpdateTaskSweep.Scheduler
        {
            Tasks = () => new[]
            {
                ("KillerUpdate-aaaa", 3, Old), ("KillerUpdate-bbbb", 1, Old), ("KillerUpdate-cccc", 2, Old),
                ("KillerUpdate-dddd", 4, Old), ("killerupdate-eeee", 3, Old), ("OtherVendorTask", 3, Old), ("NotKillerUpdate-ffff", 3, Old),
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
            Tasks = () => new[] { ("KillerUpdate-aaaa", 3, Old), ("KillerUpdate-bbbb", 3, Old) },
            Delete = name => { if (name.EndsWith("aaaa")) throw new System.UnauthorizedAccessException(); },
        };
        Assert.Equal(1, UpdateTaskSweep.Run(scheduler));
    }
}
