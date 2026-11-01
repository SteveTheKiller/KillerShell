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

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern System.IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    private static bool Reaches(string klid, Key key, bool rightAlt)
    {
        var hkl = LoadKeyboardLayout(klid, 0);
        Assert.NotEqual(System.IntPtr.Zero, hkl);
        return CtrlAltChord.ReachesChain(true, true, key, rightAlt, new LayoutKeyTextProbe(hkl).HasAltGr());
    }

    [Fact]
    public void GermanAltGrOUmlautDoesNotStartElevation()
    {
        // The O-umlaut key is Key.OemTilde on German: left Ctrl+Alt+` elevates, AltGr+O-umlaut must not.
        Assert.False(Reaches("00000407", Key.OemTilde, rightAlt: true));
        Assert.True(Reaches("00000407", Key.OemTilde, rightAlt: false));
    }

    [Fact]
    public void AltGrEOnAnAltGrLayoutDoesNotExport()
    {
        Assert.False(Reaches("00000407", Key.E, rightAlt: true));
        // French has an AltGr layer too, and AltGr+E is not a chord there either.
        Assert.False(Reaches("0000040C", Key.E, rightAlt: true));
    }

    [Fact]
    public void UsRightCtrlAltEStillExports()
    {
        Assert.True(Reaches("00000409", Key.E, rightAlt: true));
        Assert.True(Reaches("00000409", Key.OemTilde, rightAlt: true));
    }

    [Fact]
    public void LayoutAltGrAnswerIsPerLayout()
    {
        Assert.True(new LayoutKeyTextProbe(LoadKeyboardLayout("00000407", 0)).HasAltGr());
        Assert.False(new LayoutKeyTextProbe(LoadKeyboardLayout("00000409", 0)).HasAltGr());
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
