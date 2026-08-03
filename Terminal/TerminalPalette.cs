using System;
using System.Windows;
using System.Windows.Media;

// Colors for a terminal tab.
//
// Two skins. The default one is not a stock terminal palette: it takes its background and
// text from the live theme so a shell tab looks like the pane it sits beside, and only the
// 16 ANSI slots are fixed (they have to be - a script that prints "red" means red, and the
// theme has no say in it). The LCD skin is the opposite, a closed world of one phosphor,
// which is exactly what makes a CMD tab identifiable at a glance.
namespace KillerShell.Terminal
{
    internal enum TerminalSkin { Default, Lcd }

    internal sealed class TerminalPalette
    {
        public Color Background { get; private set; }
        public Color Foreground { get; private set; }
        public Color Cursor     { get; private set; }
        public Color Selection  { get; private set; }

        /// <summary>The 16 ANSI slots: 0-7 normal, 8-15 bright.</summary>
        public Color[] Ansi { get; } = new Color[16];

        /// <summary>Horizontal scanlines over the whole surface. LCD only.</summary>
        public bool Scanlines { get; private set; }

        /// <summary>Phosphor bleed under the glyphs, 0 for none. LCD only.</summary>
        public double Glow { get; private set; }

        public TerminalSkin Skin { get; private set; }

        // ═══════════════════════════════════════════════════════════
        //  PER-THEME ANSI RAMPS
        // ═══════════════════════════════════════════════════════════
        // One set of sixteen per theme, because the default skin draws on the THEME's pane and a
        // single ramp cannot sit on all six. The pane colors they are tuned against:
        //
        //   Dark #3a3a3a   Black #161616   Blood #321416
        //   Cyanotic #002e48   Greed #003824   Light #c8c8c8
        //
        // Slot order is the xterm one: 0-7 normal, 8-15 bright, each block
        // black/red/green/yellow/blue/magenta/cyan/white.
        //
        // Light is the one that is not just a brightness tweak. On a #c8c8c8 pane the whole
        // scheme has to invert its polarity - the colors go dark and saturated, and the two
        // "white" slots become dark grays, because on a light background "white" is what the
        // text is drawn ON, not what it is drawn IN. A ramp built for dark and merely dimmed
        // would leave slots 7 and 15 invisible.
        //
        // The contrast guard below is the safety net, not the plan: it only ever fires when a
        // script asks for something these ramps do not cover.
        private static readonly Color[] AnsiDark =
        [
            C(0x1C1C1C), C(0xD64545), C(0x4FB55E), C(0xD6A03C),
            C(0x4A9FE0), C(0xB07BD6), C(0x3FB5AD), C(0xC8C8C8),
            C(0x5A5A5A), C(0xF06A6A), C(0x74D97F), C(0xF0C862),
            C(0x74BEF0), C(0xCFA0F0), C(0x6FD9D2), C(0xF2F2F2),
        ];

        private static readonly Color[] AnsiBlack =
        [
            C(0x000000), C(0xE05252), C(0x4FD46A), C(0xE0AC42),
            C(0x5AAEF0), C(0xC084E8), C(0x45CFC4), C(0xD0D0D0),
            C(0x4A4A4A), C(0xFF7A7A), C(0x86F094), C(0xFFD470),
            C(0x8CCCFF), C(0xDEB0FF), C(0x7FF0E6), C(0xFFFFFF),
        ];

        // Warm-shifted: a stock red on a dark red pane vibrates rather than reads.
        private static readonly Color[] AnsiBlood =
        [
            C(0x1A0A0B), C(0xF05A62), C(0x5FC271), C(0xE8AE4A),
            C(0x6FAEE8), C(0xC58AE0), C(0x5AC4BC), C(0xF0DCD0),
            C(0x5C3436), C(0xFF8A8A), C(0x82DC90), C(0xFFCC72),
            C(0x92C8F5), C(0xDDA8F0), C(0x82DCD6), C(0xFFF4EC),
        ];

        // Blues lifted well clear of a #002e48 pane, which a stock blue sinks into.
        private static readonly Color[] AnsiCyanotic =
        [
            C(0x04121C), C(0xE85F63), C(0x4FC98A), C(0xE6B44C),
            C(0x62B9F5), C(0xB98CE8), C(0x4FD1D8), C(0xDCE8EE),
            C(0x2A4A5E), C(0xFF8288), C(0x74E0A2), C(0xFFD076),
            C(0x8FD4FF), C(0xD3AEFF), C(0x84E8EE), C(0xF4FBFF),
        ];

        // Greens lifted, for the same reason as Cyanotic's blues.
        private static readonly Color[] AnsiGreed =
        [
            C(0x04160E), C(0xE85F5F), C(0x62D98A), C(0xE0B44A),
            C(0x5FAFE0), C(0xB486DE), C(0x4FCFB8), C(0xDDEAD8),
            C(0x28503C), C(0xFF8181), C(0x8CF0AE), C(0xFFCE72),
            C(0x8CC9F0), C(0xD0A8F5), C(0x7CE8D4), C(0xF2FFF0),
        ];

        private static readonly Color[] AnsiLight =
        [
            C(0x2B2B2B), C(0xB3261E), C(0x1B6E2F), C(0x8A5B00),
            C(0x1C5FA8), C(0x7B2CA8), C(0x116E6E), C(0x4A4A4A),
            C(0x5A5A5A), C(0xD13A2E), C(0x24913F), C(0xA87400),
            C(0x2678CC), C(0x9640C8), C(0x168C8C), C(0x1A1A1A),
        ];

        private static Color[] AnsiFor(Services.Theme theme) => theme switch
        {
            Services.Theme.Black    => AnsiBlack,
            Services.Theme.Blood    => AnsiBlood,
            Services.Theme.Cyanotic => AnsiCyanotic,
            Services.Theme.Greed    => AnsiGreed,
            Services.Theme.Light    => AnsiLight,
            _                       => AnsiDark,
        };

        private static Color C(int rgb) =>
            Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        public static TerminalPalette For(TerminalSkin skin)
        {
            var p = new TerminalPalette { Skin = skin };

            if (skin == TerminalSkin.Lcd)
            {
                // One phosphor, lit to different levels. Everything the shell asks for lands
                // somewhere on this ramp, so a colored prompt still reads as a colored prompt
                // without ever leaving the green.
                p.Background = C(0x0A140C);
                p.Foreground = C(0x4CE07A);
                p.Cursor     = C(0x9BFFC0);
                p.Selection  = Color.FromArgb(0x55, 0x4C, 0xE0, 0x7A);
                p.Scanlines  = true;
                p.Glow       = 0.55;

                int[] ramp =
                [
                    0x08120A, 0x2E7A47, 0x4CE07A, 0x8FE86A, 0x2FA85F, 0x63E8A8, 0x3FD9C0, 0x7FD99A,
                    0x1A3D24, 0x54A86B, 0x7BFFA0, 0xC8FF8A, 0x4FD98A, 0x92FFD0, 0x6FFFE0, 0xD8FFE4,
                ];
                for (int i = 0; i < 16; i++) p.Ansi[i] = C(ramp[i]);
                return p;
            }

            Array.Copy(AnsiFor(Services.ThemeManager.Current), p.Ansi, 16);

            // Theme colors, resolved once at construction. A theme switch rebuilds the palette
            // rather than tracking a DynamicResource per cell, which would be absurd.
            // SurfaceBrush, not PaneBrush (Steve, 2026-08-02): the shell's own screen reads as a
            // panel sunk slightly BELOW the pane surface it sits in, the same "elevated but not
            // stark" step KillerPDF's floating annotation bars use - SurfaceBrush already sits
            // between BackgroundBrush and PaneBrush in every theme (dialogs, dropdowns, the
            // FileDialog previews), so this is the family's existing answer to that step, not a
            // new color.
            p.Background = Res("SurfaceBrush", C(0x1E1E1E));
            p.Foreground = Res("TextBrush",    C(0xE0E0E0));
            p.Cursor     = Res("PrimaryBrush", C(0x50AEE8));
            var sel = p.Cursor;
            p.Selection  = Color.FromArgb(0x55, sel.R, sel.G, sel.B);
            return p;
        }

        private static Color Res(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b.Color;
            return fallback;
        }

        /// <summary>
        /// Turn a cell's stored color into something drawable. Palette indexes 16 to 255 are
        /// the xterm cube and gray ramp, computed rather than tabulated.
        /// </summary>
        public Color Resolve(int color, bool background)
        {
            if (color == TerminalBuffer.DefaultColor) return background ? Background : Foreground;

            if ((color & TerminalBuffer.RgbFlag) != 0)
            {
                int rgb = color & 0xFFFFFF;
                var c = C(rgb);
                // On LCD nothing escapes the phosphor: a truecolor escape becomes its
                // brightness on the green ramp instead of punching a hole in the effect.
                return Skin == TerminalSkin.Lcd ? Phosphor(Luma(c)) : c;
            }

            if (color < 16) return Ansi[color];

            if (color < 232)
            {
                int i = color - 16;
                int r = i / 36, g = (i / 6) % 6, b = i % 6;
                var c = Color.FromRgb(Step(r), Step(g), Step(b));
                return Skin == TerminalSkin.Lcd ? Phosphor(Luma(c)) : c;
            }

            byte v = (byte)(8 + (color - 232) * 10);
            var gray = Color.FromRgb(v, v, v);
            return Skin == TerminalSkin.Lcd ? Phosphor(v / 255.0) : gray;
        }

        private static byte Step(int i) => (byte)(i == 0 ? 0 : 55 + i * 40);

        // ═══════════════════════════════════════════════════════════
        //  CONTRAST GUARD
        // ═══════════════════════════════════════════════════════════
        // The 16 ANSI slots are fixed and tuned for a DARK terminal, but the default skin takes
        // its background from the live theme - so on Light the shell's "white" (0xC8C8C8) lands
        // on a near-white pane and vanishes, and on Blood its red sits on a dark red pane at
        // almost no contrast. Neither is something the shell can be asked to fix: a script that
        // prints red means red, and it has no idea what theme it is being drawn into.
        //
        // So the pairing is corrected at draw time instead. HUE IS PRESERVED - only lightness
        // moves, and only far enough to clear the threshold - so red stays recognisably red and
        // a prompt's colors still mean what the script intended.
        private const double MinContrast = 4.0;   // WCAG AA is 4.5 for body text; a terminal is
                                                  // dense mono at a size where 4 is comfortable,
                                                  // and higher starts washing hues to white.

        /// <summary>
        /// A readable version of <paramref name="fg"/> against <paramref name="bg"/>.
        /// </summary>
        /// <remarks>
        /// Never applied to the LCD skin: that one is a closed ramp built from a single phosphor
        /// and every level on it already reads against its own background. Correcting it would
        /// only bleach the effect that makes a CMD tab identifiable.
        /// </remarks>
        public Color Readable(Color fg, Color bg)
        {
            if (Skin == TerminalSkin.Lcd) return fg;
            if (Contrast(fg, bg) >= MinContrast) return fg;

            // Away from the background, not toward a fixed white or black: on a mid-gray pane
            // the nearer extreme is the one that gets there with the least distortion.
            var target = Relative(bg) > 0.18 ? Colors.Black : Colors.White;

            // Sixteen steps is finer than the eye resolves at this size and cheap enough to run
            // per distinct color per frame - the caller only asks once per run of like cells.
            for (int i = 1; i <= 16; i++)
            {
                var mixed = Mix(fg, target, i / 16.0);
                if (Contrast(mixed, bg) >= MinContrast) return mixed;
            }
            return target;   // bg is mid-gray enough that nothing clears it; take the extreme
        }

        private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

        private static double Contrast(Color a, Color b)
        {
            double la = Relative(a), lb = Relative(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        // WCAG relative luminance: sRGB gamma undone per channel, then the usual weights. Not
        // the same thing as Luma below, which is a cheap perceptual brightness for the phosphor
        // ramp and does not need to be accurate about gamma.
        private static double Relative(Color c) =>
            0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);

        private static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        private Color Phosphor(double level)
        {
            level = level < 0 ? 0 : level > 1 ? 1 : level;
            var lo = Background;
            var hi = Ansi[10];
            return Color.FromRgb(
                (byte)(lo.R + (hi.R - lo.R) * level),
                (byte)(lo.G + (hi.G - lo.G) * level),
                (byte)(lo.B + (hi.B - lo.B) * level));
        }
    }
}
