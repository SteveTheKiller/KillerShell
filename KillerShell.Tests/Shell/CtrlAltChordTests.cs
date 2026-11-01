using System.Windows.Input;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class CtrlAltChordTests
{
    [Theory]
    [InlineData(Key.C)]
    [InlineData(Key.X)]
    [InlineData(Key.V)]
    [InlineData(Key.A)]
    [InlineData(Key.D)]
    [InlineData(Key.N)]
    [InlineData(Key.Left)]
    public void CtrlAltChordsOutsideTheChainStopBeforeIt(Key key)
    {
        Assert.False(CtrlAltChord.ReachesChain(ctrl: true, alt: true, key));
    }

    [Theory]
    [InlineData(Key.E)]
    [InlineData(Key.OemTilde)]
    public void ExportAndElevatedShellChordsReachTheChain(Key key)
    {
        Assert.True(CtrlAltChord.ReachesChain(ctrl: true, alt: true, key));
    }

    [Theory]
    [InlineData(false, false, Key.C)]
    [InlineData(true, false, Key.C)]
    [InlineData(false, true, Key.C)]
    public void OtherModifierCombinationsAlwaysReachTheChain(bool ctrl, bool alt, Key key)
    {
        Assert.True(CtrlAltChord.ReachesChain(ctrl, alt, key));
    }
}
