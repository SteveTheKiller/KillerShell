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

        /// <summary>
        /// As above, plus the AltGr rule: with the right Alt key down on a layout that has an AltGr
        /// layer at all, Ctrl+Alt is that layer and never a chord, even for a key the layer leaves
        /// empty (AltGr+O-umlaut on German is the same physical key as Ctrl+Alt+`, and AltGr+E must
        /// not export on a layout that types nothing there). A layout with no AltGr layer, US
        /// included, keeps its right Ctrl+Alt chords.
        /// </summary>
        public static bool ReachesChain(bool ctrl, bool alt, Key key, bool rightAltDown, bool layoutHasAltGr)
            => !(ctrl && alt && rightAltDown && layoutHasAltGr) && ReachesChain(ctrl, alt, key);
    }
}
