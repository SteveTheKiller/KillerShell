using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TerminalSpaceKeyTests
{
    [Fact]
    public void CtrlSpaceSendsNul()
    {
        Assert.Equal("\0", TerminalControl.SpaceBytes(ctrl: true, alt: false));
    }

    [Fact]
    public void CtrlAltSpaceSendsNothing()
    {
        Assert.Null(TerminalControl.SpaceBytes(ctrl: true, alt: true));
    }

    [Fact]
    public void PlainAndAltSpaceSendNothing()
    {
        Assert.Null(TerminalControl.SpaceBytes(ctrl: false, alt: false));
        Assert.Null(TerminalControl.SpaceBytes(ctrl: false, alt: true));
    }
}
