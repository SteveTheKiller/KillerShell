using System.Windows.Input;

namespace KillerShell.Shell
{
    /// <summary>
    /// Which Ctrl+Alt chords the window handler's chain acts on after HandleCommandKey.
    /// </summary>
    public static class CtrlAltChord
    {
        public static bool ReachesChain(bool ctrl, bool alt, Key key)
            => !(ctrl && alt) || key == Key.E || key == Key.OemTilde;
    }
}
