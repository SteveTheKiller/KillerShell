using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

// The folder tree's "being driven, do not navigate" guard. Two hidden-file toggles that overlap
// finish in either order, and the tree must not be left believing it is still syncing, which
// would stop clicks on it from ever navigating again.
public sealed class TreeSyncGateTests
{
    [Fact]
    public void OverlappingScopesThatFinishInReverseOrderEndInactive()
    {
        var gate = new TreeSyncGate();
        var first = gate.Enter();
        var second = gate.Enter();
        first.Dispose();
        Assert.True(gate.IsActive);
        second.Dispose();
        Assert.False(gate.IsActive);
    }

    [Fact]
    public void DisposingAScopeTwiceDoesNotReleaseAnother()
    {
        var gate = new TreeSyncGate();
        var outer = gate.Enter();
        var inner = gate.Enter();
        inner.Dispose();
        inner.Dispose();
        Assert.True(gate.IsActive);
        outer.Dispose();
        Assert.False(gate.IsActive);
    }
}
