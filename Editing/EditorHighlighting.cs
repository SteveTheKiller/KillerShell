using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using KillerShell.Terminal;

// Making the shipped syntax colors readable on this app's themes.
//
// AvalonEdit's .xshd files were written for a WHITE editor: Blue strings, Maroon variables,
// MidnightBlue commands, DarkBlue numbers, Navy keywords. On a #3a3a3a pane half of them are
// barely there and on Black they are a rumour. The definitions are not wrong - they were written
// against a background this app does not have.
//
// So the HUE is kept and only the lightness moves, and only as far as it has to. That is the
// same judgement the terminal already makes about a script's ANSI colors, for the same reason
// and with the same code (TerminalPalette.Readable): red should still read as red, and nobody
// authoring a highlighting definition had any idea what it would be drawn on.
//
// The shipped color is remembered the first time each one is touched, and every later pass
// starts from THAT rather than from the last result - otherwise switching Dark to Light and back
// would ratchet a color further from where it began on every single switch.
namespace KillerShell.Editing
{
    internal static class EditorHighlighting
    {
        // "definition name/color name" -> the foreground the .xshd shipped with. Keyed by name
        // rather than by the HighlightingColor object: that type implements IEquatable by VALUE,
        // so two unrelated colors that happen to share a foreground would land in one bucket and
        // overwrite each other's original.
        private static readonly Dictionary<string, Color> Shipped = new();

        // Definition name -> the background its colors were last lifted against. Keyed by NAME for
        // the same reason Shipped is: the vendored AvalonEdit makes no promise about reference
        // identity or value equality for a definition object, and two definitions sharing a name
        // are the same language. See the early return in MakeReadable.
        private static readonly Dictionary<string, Color> LastBackground = new();

        /// <summary>
        /// Lift every color in <paramref name="definition"/> clear of <paramref name="background"/>.
        /// Safe to call repeatedly, and on every theme switch.
        /// </summary>
        /// <remarks>
        /// The definitions are cached one per language by HighlightingManager and shared by every
        /// open document, so this recolors all of them at once - which is what is wanted, since
        /// there is only ever one theme on.
        /// </remarks>
        internal static void MakeReadable(IHighlightingDefinition? definition, Color background)
        {
            if (definition == null) return;

            // ONE pass per definition per background, not one per open document.
            //
            // The remarks above are the whole reason this guard is safe AND the reason it is
            // needed: a definition is a HighlightingManager singleton shared by every editor, so
            // recoloring it once recolors every document using it. But RefreshEditorThemes
            // (EditorTabs.cs) still calls ApplyTheme on every tab in BOTH panes, and each of those
            // calls came back through here and walked the entire NamedHighlightingColors list
            // again to write the values that were already sitting there. Several documents open on
            // one language meant that walk N times over, plus a TerminalPalette built N times to
            // ask it the same question - all of it inside the pause between the theme swap and the
            // crossfade, where it is time the user is watching (ThemeFlyout.cs CrossfadeSwap).
            //
            // An UNNAMED definition opts out rather than sharing the empty-string bucket with
            // every other unnamed one: colliding there would silently skip a definition that had
            // never been lifted at all, which is worse than repeating the work.
            string defKey = definition.Name ?? string.Empty;
            bool memoizable = defKey.Length > 0;
            if (memoizable && LastBackground.TryGetValue(defKey, out Color already) && already == background)
                return;

            // The contrast guard lives on the terminal palette because that is where it was
            // needed first. Reused rather than copied: one implementation means the two surfaces
            // cannot start disagreeing about what counts as readable.
            var guard = TerminalPalette.For(TerminalSkin.Default);

            foreach (var color in definition.NamedHighlightingColors)
            {
                Color? shipped = Original(definition.Name, color);
                if (shipped == null) continue;

                color.Foreground = new SimpleHighlightingBrush(guard.Readable(shipped.Value, background));
            }

            // Recorded only after the pass completes, so a throw part-way leaves the definition
            // marked as un-lifted and the next theme switch redoes it rather than trusting a
            // half-applied result.
            if (memoizable) LastBackground[defKey] = background;
        }

        /// <summary>
        /// The foreground this color was loaded with, or null when it never had one - plenty of
        /// them only set a weight or a style, and those have nothing to correct.
        /// </summary>
        private static Color? Original(string? definitionName, HighlightingColor color)
        {
            // Both halves are optional in the .xshd schema, and an unnamed color in an unnamed
            // definition is still a real entry that has to keep its own slot rather than share
            // one with every other anonymous pair.
            string key = (definitionName ?? string.Empty) + "/" + (color.Name ?? string.Empty);
            if (Shipped.TryGetValue(key, out Color known)) return known;

            Color? current = color.Foreground?.GetColor(null);
            if (current == null) return null;

            Shipped[key] = current.Value;
            return current.Value;
        }
    }
}
