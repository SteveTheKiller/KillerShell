using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Pictures for the fabricated machine's image files, DRAWN at run time from the file's own path.
//
// The problem this solves: everything about the fabricated machine (Services\DemoFileSystem.cs) is
// a table, not a disk. A row named "fairview-rack-front.jpg" has no bytes anywhere, so the
// thumbnail decode had nothing to decode and every image row fell back to the shared JPEG glyph -
// which meant the two views whose entire selling point is showing you the picture were the two
// views demo mode could not demonstrate.
//
// Nothing ships and nothing is probed. An earlier version loaded photographs from a folder on
// disk, which put a real user directory into the source of a public repository and made a capture
// depend on the one machine that folder existed on. Both are wrong here, and the second one is
// wrong twice over for a mode whose whole promise is that it never reads real data. Every pixel
// below is arithmetic - a smooth two-octave value-noise field run through a color ramp - so an
// image row shows an image without an image existing anywhere.
//
// The result is a pure function of the fabricated path. Same picture on every launch, on every
// machine and in every view, so the icon view's tile and the details strip's preview for one file
// cannot disagree, and a capture retaken next month matches the one taken today. That is the same
// intent as the fixed RNG seed in DemoMode.cs and the fixed sizes and dates in DemoFileSystem.cs.
namespace KillerShell.Services
{
    internal static class DemoImages
    {
        private static readonly object Gate = new();

        // Every fabricated path that gets a picture, taken from the fabricated machine's own fixed
        // walk. A row the listing draws an image icon for is exactly a row this answers, so
        // nothing is left in a broken half-state.
        private static HashSet<string>? _known;

        // Keyed by path AND size. The listing asks for a small tile and the details strip asks for
        // a much larger preview; drawing the large one to answer a 32px tile would cost many times
        // what the tile is worth.
        private static readonly Dictionary<string, BitmapSource> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        // Noise cells across the image. Few and large: at 32px a fine lattice averages out to flat
        // gray, and the tile has to read as a picture at exactly that size.
        private const int Lattice = 4;

        // Hue families that read as photographic rather than as a color chart. Picked per image
        // from the path hash, so neighboring tiles in a folder do not come out the same color.
        private static readonly double[] Hues =
            [14, 32, 45, 92, 140, 168, 190, 208, 224, 262, 288, 336];

        /// <summary>
        /// The picture for a fabricated image path at <paramref name="px"/> on its longest side,
        /// or null when the path is not one of the fabricated machine's pictures. Frozen, so the
        /// background threads the listing and the details strip decode on can hand it to the UI
        /// thread directly.
        /// </summary>
        internal static BitmapSource? Render(string fakePath, int px)
        {
            if (string.IsNullOrEmpty(fakePath) || px <= 0) return null;

            string key = fakePath + "|" + px;

            HashSet<string> known;
            lock (Gate)
            {
                _known ??= BuildKnown();
                known = _known;
                if (Cache.TryGetValue(key, out var hit)) return hit;
            }

            if (!known.Contains(fakePath)) return null;

            // Drawn OUTSIDE the lock: two threads racing on the same tile draw the same pixels,
            // because the image is a function of the path, so the wasted work is one tile and the
            // alternative is every listing thread queueing behind whichever one is drawing.
            var img = Draw(fakePath, px);

            lock (Gate) Cache[key] = img;
            return img;
        }

        private static HashSet<string> BuildKnown()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in DemoFs.ImagePaths) set.Add(p);
            return set;
        }

        private static BitmapSource Draw(string path, int px)
        {
            uint seed = Hash(path.ToLowerInvariant());

            // Portrait for one image in four, so a tile grid is not one repeated shape. The
            // longest side is px either way, which is the dimension both callers size against.
            bool portrait = (seed & 3u) == 0u;
            int w = portrait ? Math.Max(1, px * 3 / 4) : px;
            int h = portrait ? px : Math.Max(1, px * 3 / 4);

            // Two lattices of independent values, drawn from the path seed. The second is twice
            // as fine and mixed in at a quarter weight, which is what stops the result reading as
            // one smooth blob.
            uint rng = seed;
            var coarse = new double[Lattice + 1, Lattice + 1];
            var fine   = new double[(2 * Lattice) + 1, (2 * Lattice) + 1];
            for (int y = 0; y <= Lattice; y++)
                for (int x = 0; x <= Lattice; x++)
                    coarse[y, x] = NextUnit(ref rng);
            for (int y = 0; y <= 2 * Lattice; y++)
                for (int x = 0; x <= 2 * Lattice; x++)
                    fine[y, x] = NextUnit(ref rng);

            // Three stops of one hue family: a deep shade, a mid tone and a near-white highlight.
            // The hue drifts across the ramp rather than staying put, the way light through a real
            // scene does - a single-hue ramp reads as a gradient swatch.
            double hue = Hues[(int)(seed % (uint)Hues.Length)];
            HsvToRgb(hue,                  0.58, 0.26, out double dr, out double dg, out double db);
            HsvToRgb(Wrap(hue + 18.0),     0.48, 0.62, out double mr, out double mg, out double mb);
            HsvToRgb(Wrap(hue + 42.0),     0.18, 0.96, out double lr, out double lg, out double lb);

            int stride = w * 4;
            var pixels = new byte[stride * h];

            for (int y = 0; y < h; y++)
            {
                double v = h > 1 ? y / (double)(h - 1) : 0.0;

                // Light falls from the top, which is the one cue that makes an abstract field read
                // as a photograph rather than as a texture.
                double fall = 1.12 - (0.30 * v);

                for (int x = 0; x < w; x++)
                {
                    double u = w > 1 ? x / (double)(w - 1) : 0.0;

                    double n = (0.75 * Sample(coarse, Lattice, u, v))
                             + (0.25 * Sample(fine, 2 * Lattice, u, v));
                    if (n < 0.0) n = 0.0;
                    else if (n > 1.0) n = 1.0;

                    double r, g, b;
                    if (n < 0.5)
                    {
                        double t = n * 2.0;
                        r = dr + ((mr - dr) * t);
                        g = dg + ((mg - dg) * t);
                        b = db + ((mb - db) * t);
                    }
                    else
                    {
                        double t = (n - 0.5) * 2.0;
                        r = mr + ((lr - mr) * t);
                        g = mg + ((lg - mg) * t);
                        b = mb + ((lb - mb) * t);
                    }

                    // Corners darker than the middle, the way a lens vignettes.
                    double dx = u - 0.5, dy = v - 0.5;
                    double shade = fall * (1.0 - (1.10 * ((dx * dx) + (dy * dy))));

                    // A little grain, so a flat area is not a plate of one exact value. Hashed
                    // from the coordinates rather than drawn from a generator, so it does not
                    // depend on the order the pixels happen to be walked in.
                    int grain = (int)(Mix(seed, x, y) % 13u) - 6;

                    int i = (y * stride) + (x * 4);
                    pixels[i]     = Clamp((b * 255.0 * shade) + grain);
                    pixels[i + 1] = Clamp((g * 255.0 * shade) + grain);
                    pixels[i + 2] = Clamp((r * 255.0 * shade) + grain);
                    pixels[i + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Bilinear value noise with a smoothstep on both axes, so the cell boundaries do
        /// not show as creases.</summary>
        private static double Sample(double[,] lattice, int cells, double u, double v)
        {
            double fx = u * cells, fy = v * cells;

            int x0 = (int)fx, y0 = (int)fy;
            if (x0 >= cells) x0 = cells - 1;
            if (y0 >= cells) y0 = cells - 1;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;

            double tx = Smooth(fx - x0), ty = Smooth(fy - y0);

            double top    = lattice[y0, x0]         + ((lattice[y0, x0 + 1]     - lattice[y0, x0])         * tx);
            double bottom = lattice[y0 + 1, x0]     + ((lattice[y0 + 1, x0 + 1] - lattice[y0 + 1, x0])     * tx);
            return top + ((bottom - top) * ty);
        }

        private static double Smooth(double t)
        {
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            return t * t * (3.0 - (2.0 * t));
        }

        private static byte Clamp(double value)
            => value <= 0.0 ? (byte)0 : value >= 255.0 ? (byte)255 : (byte)value;

        private static double Wrap(double hue) => hue >= 360.0 ? hue - 360.0 : hue;

        private static void HsvToRgb(double hue, double sat, double val,
                                     out double r, out double g, out double b)
        {
            double c = val * sat;
            double hp = hue / 60.0;
            double x = c * (1.0 - Math.Abs((hp % 2.0) - 1.0));
            double m = val - c;

            double r1, g1, b1;
            if      (hp < 1.0) { r1 = c; g1 = x; b1 = 0; }
            else if (hp < 2.0) { r1 = x; g1 = c; b1 = 0; }
            else if (hp < 3.0) { r1 = 0; g1 = c; b1 = x; }
            else if (hp < 4.0) { r1 = 0; g1 = x; b1 = c; }
            else if (hp < 5.0) { r1 = x; g1 = 0; b1 = c; }
            else               { r1 = c; g1 = 0; b1 = x; }

            r = r1 + m;
            g = g1 + m;
            b = b1 + m;
        }

        /// <summary>FNV-1a. Never zero: the generator below is an xorshift, and zero is its one
        /// fixed point - a path that hashed to it would draw a flat black rectangle.</summary>
        private static uint Hash(string s)
        {
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return h == 0u ? 1u : h;
        }

        private static double NextUnit(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFFu) / (double)0x1000000;
        }

        private static uint Mix(uint seed, int x, int y)
        {
            unchecked
            {
                uint h = seed ^ (uint)(x * 374761393) ^ (uint)(y * 668265263);
                h ^= h >> 13;
                h *= 1274126177u;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
