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
