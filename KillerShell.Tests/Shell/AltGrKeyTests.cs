using System.Windows.Input;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class AltGrKeyTests
{
    private const ModifierKeys CtrlAlt = ModifierKeys.Control | ModifierKeys.Alt;

    [Fact]
    public void CtrlAltWithRightAltDownIsText()
    {
        Assert.True(AltGrKey.IsText(CtrlAlt, rightAltDown: true));
    }

    [Theory]
    [InlineData(ModifierKeys.None, false)]
    [InlineData(ModifierKeys.Control, false)]
    [InlineData(ModifierKeys.Alt, true)]
    [InlineData(ModifierKeys.Alt, false)]
    [InlineData(ModifierKeys.Control, true)]
    [InlineData(ModifierKeys.Shift, true)]
    [InlineData(CtrlAlt, false)]
    public void EverythingElseIsAChordOrPlainKey(ModifierKeys modifiers, bool rightAltDown)
    {
        Assert.False(AltGrKey.IsText(modifiers, rightAltDown));
    }

    [Fact]
    public void ShiftDoesNotChangeTheAnswer()
    {
        Assert.True(AltGrKey.IsText(CtrlAlt | ModifierKeys.Shift, rightAltDown: true));
        Assert.False(AltGrKey.IsText(CtrlAlt | ModifierKeys.Shift, rightAltDown: false));
    }
}
