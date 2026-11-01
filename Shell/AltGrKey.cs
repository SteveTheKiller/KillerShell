using System.Windows.Input;

namespace KillerShell.Shell
{
    /// <summary>
    /// AltGr reaches WPF as Ctrl+Alt with the right Alt key down and e.Key the real key
    /// (AltGr+0 is "}" on German and Nordic layouts, AltGr+E is the euro sign). That
    /// is text, never a chord, wherever the focus is, so Window_PreviewKeyDown returns before any
    /// chord gate or branch looks at it. Left Ctrl+Alt is a real chord (Ctrl+Alt+E export).
    /// </summary>
    public static class AltGrKey
    {
        public static bool IsText(ModifierKeys modifiers, bool rightAltDown)
            => (modifiers & ModifierKeys.Control) != 0
                && (modifiers & ModifierKeys.Alt) != 0
                && rightAltDown;
    }
}
