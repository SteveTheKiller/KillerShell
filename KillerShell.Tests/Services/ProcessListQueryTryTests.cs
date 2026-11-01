using System.Diagnostics;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ProcessListQueryTryTests
{
    [Fact]
    public void ProcessQuerySaysWhenWmiAnswered()
    {
        Assert.True(ProcessListQueryService.TryQueryProcesses(out var details));
        Assert.True(details.ContainsKey(Process.GetCurrentProcess().Id));
    }

    [Fact]
    public void ProcessQuerySaysWhenWmiDidNotAnswer()
    {
        Assert.False(ProcessListQueryService.TryQueryProcesses(out var details, @"root\ks-no-such-namespace"));
        Assert.Empty(details);
    }

    [Fact]
    public void ServiceQuerySaysWhenWmiAnswered()
    {
        Assert.True(ProcessListQueryService.TryQueryServices(out var details));
        Assert.True(details.ContainsKey("RpcSs"));
    }

    [Fact]
    public void ServiceQuerySaysWhenWmiDidNotAnswer()
    {
        Assert.False(ProcessListQueryService.TryQueryServices(out var details, @"root\ks-no-such-namespace"));
        Assert.Empty(details);
    }
}
