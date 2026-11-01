using System;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace KillerShell.Shell
{
    /// <summary>
    /// AltGr reaches WPF as Ctrl+Alt with the right Alt key down and e.Key the real key
    /// (AltGr+0 is "}" on German and Nordic layouts, AltGr+E is the euro sign). That
    /// is text, never a chord, wherever the focus is, so Window_PreviewKeyDown returns before any
    /// chord gate or branch looks at it. That holds only for a key the layout gives a character
    /// with AltGr held (a US layout has none, so its Ctrl+Alt+E stays the export chord).
    /// Left Ctrl+Alt is a real chord (Ctrl+Alt+E export).
    /// </summary>
    public static class AltGrKey
    {
        public static bool IsText(ModifierKeys modifiers, bool rightAltDown, bool keyProducesChar)
            => (modifiers & ModifierKeys.Control) != 0
                && (modifiers & ModifierKeys.Alt) != 0
                && rightAltDown
                && keyProducesChar;

        /// <summary>Whether this key yields a character with AltGr held. IME and dead-key
        /// input is text already; anything else is asked of the layout.</summary>
        public static bool KeyProducesChar(Key key, bool shift, IKeyTextProbe probe)
        {
            if (key == Key.ImeProcessed || key == Key.DeadCharProcessed) return true;
            return probe.ProducesChar(KeyInterop.VirtualKeyFromKey(key), shift);
        }
    }

    /// <summary>Asks a keyboard layout whether a virtual key gives text with AltGr held.</summary>
    public interface IKeyTextProbe
    {
        bool ProducesChar(int virtualKey, bool shift);
    }

    public sealed class LayoutKeyTextProbe : IKeyTextProbe
    {
        private readonly IntPtr _hkl;

        public LayoutKeyTextProbe(IntPtr hkl) { _hkl = hkl; }

        public static LayoutKeyTextProbe ForCurrentLayout() => new LayoutKeyTextProbe(GetKeyboardLayout(0));

        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);
        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState,
            [Out, MarshalAs(UnmanagedType.LPWStr, SizeConst = 4)] System.Text.StringBuilder buffer,
            int bufferSize, uint flags, IntPtr hkl);

        private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkRMenu = 0xA5;

        /// <summary>ToUnicodeEx with Ctrl, Alt and right Alt down (what AltGr looks like to the
        /// layout). Flag 0x4 leaves the keyboard's dead-key state alone. Any nonzero result,
        /// a dead key's -1 included, is text.</summary>
        public bool ProducesChar(int virtualKey, bool shift)
        {
            if (virtualKey <= 0 || virtualKey > 0xFF) return false;
            var state = new byte[256];
            state[VkControl] = 0x80;
            state[VkMenu] = 0x80;
            state[VkRMenu] = 0x80;
            if (shift) state[VkShift] = 0x80;
            try
            {
                var buf = new System.Text.StringBuilder(4);
                uint scan = MapVirtualKeyEx((uint)virtualKey, 0, _hkl);
                return ToUnicodeEx((uint)virtualKey, scan, state, buf, 4, 0x4, _hkl) != 0;
            }
            catch { return false; }
        }
    }
}
