using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class EventLevelQueryTests
{
    [Fact]
    public void InformationAlsoTakesLevelZero()
    {
        // Many providers log informational events at level 0 (LogAlways), which Event Viewer
        // groups under Information, so the query for it has to take both.
        Assert.Equal("*[System[(Level=0 or Level=4)]]", EventLevelQuery.For(4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void OtherLevelsMatchExactlyOneLevel(int level)
    {
        Assert.Equal("*[System[(Level=" + level + ")]]", EventLevelQuery.For(level));
    }

    [Fact]
    public void InformationQueryParsesAndOnlyReturnsLevelZeroOrFour()
    {
        var levels = new HashSet<byte?>();
        var query = new EventLogQuery("Application", PathType.LogName, EventLevelQuery.For(4)) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (int i = 0; i < 200; i++)
        {
            using EventRecord? record = reader.ReadEvent();
            if (record == null) break;
            levels.Add(record.Level);
        }
        Assert.All(levels, level => Assert.True(level == null || level == 0 || level == 4, "Unexpected level " + level));
    }
}
