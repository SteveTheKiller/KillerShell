using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class AltGrKeyTests
{
    private const ModifierKeys CtrlAlt = ModifierKeys.Control | ModifierKeys.Alt;

    private sealed class FakeProbe : IKeyTextProbe
    {
        public Func<int, bool, bool> Answer = (_, _) => false;
        public int Calls;
        public bool ProducesChar(int virtualKey, bool shift) { Calls++; return Answer(virtualKey, shift); }
    }

    [Fact]
    public void CtrlAltWithRightAltDownAndATextKeyIsText()
    {
        Assert.True(AltGrKey.IsText(CtrlAlt, rightAltDown: true, keyProducesChar: true));
    }

    [Fact]
    public void CtrlAltWithRightAltDownOnAKeyWithNoTextIsAChord()
    {
        Assert.False(AltGrKey.IsText(CtrlAlt, rightAltDown: true, keyProducesChar: false));
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
        Assert.False(AltGrKey.IsText(modifiers, rightAltDown, keyProducesChar: true));
    }

    [Fact]
    public void ShiftDoesNotChangeTheAnswer()
    {
        Assert.True(AltGrKey.IsText(CtrlAlt | ModifierKeys.Shift, rightAltDown: true, keyProducesChar: true));
        Assert.False(AltGrKey.IsText(CtrlAlt | ModifierKeys.Shift, rightAltDown: false, keyProducesChar: true));
    }

    [Theory]
    [InlineData(Key.ImeProcessed)]
    [InlineData(Key.DeadCharProcessed)]
    public void ImeAndDeadCharKeysAreTextWithoutAskingTheLayout(Key key)
    {
        var probe = new FakeProbe();
        Assert.True(AltGrKey.KeyProducesChar(key, shift: false, probe));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void OtherKeysAskTheProbeWithTheirVirtualKeyAndShift()
    {
        int vk = KeyInterop.VirtualKeyFromKey(Key.E);
        var probe = new FakeProbe { Answer = (v, s) => v == vk && s };
        Assert.True(AltGrKey.KeyProducesChar(Key.E, shift: true, probe));
        Assert.False(AltGrKey.KeyProducesChar(Key.E, shift: false, probe));
        Assert.False(AltGrKey.KeyProducesChar(Key.Left, shift: true, probe));
    }

    // The real probe, against explicit layouts. LoadKeyboardLayout without KLF_ACTIVATE adds the
    // layout to the process without switching the thread or the user's input language.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    private static bool Text(IntPtr hkl, Key key, bool shift = false)
        => new LayoutKeyTextProbe(hkl).ProducesChar(KeyInterop.VirtualKeyFromKey(key), shift);

    [Fact]
    public void UsLayoutHasNoAltGrText()
    {
        IntPtr us = LoadKeyboardLayout("00000409", 0);
        Assert.NotEqual(IntPtr.Zero, us);
        Assert.False(Text(us, Key.E));
        Assert.False(Text(us, Key.D7));
        Assert.False(Text(us, Key.Left));
    }

    [Fact]
    public void GermanLayoutAltGrKeysAreText()
    {
        IntPtr de = LoadKeyboardLayout("00000407", 0);
        Assert.NotEqual(IntPtr.Zero, de);
        Assert.True(Text(de, Key.E));
        Assert.True(Text(de, Key.D7));
        Assert.True(Text(de, Key.Q));
        Assert.False(Text(de, Key.Left));
    }
}
