using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Pictures for the fabricated machine's image files: real photographs when they are sitting beside
// the repo, and a drawn field when they are not.
//
// The problem this solves: everything about the fabricated machine (Services\DemoFileSystem.cs) is
// a table, not a disk. A row named "fairview-rack-front.jpg" has no bytes anywhere, so the
// thumbnail decode had nothing to decode and every image row fell back to the shared JPEG glyph -
// which meant the two views whose entire selling point is showing you the picture were the two
// views demo mode could not demonstrate.
//
// TWO SOURCES, tried in this order:
//
// 1. A photograph from code\Demo\KillerShell, which is where demo material for the family lives
//    (KillerNotes reads its own folder beside this one). That folder sits OUTSIDE the repo and is
//    never committed or shipped, so no photograph and no personal directory reaches the public
//    source. The assignment is by INDEX into the fabricated machine's fixed walk, not by name, so
//    the file names on screen stay invented while the pixels behind them are real.
// 2. Failing that - no folder, or a file with no codec - a picture DRAWN from the path. Every
//    pixel is arithmetic, a smooth two-octave value-noise field run through a color ramp, so an
//    image row still shows an image. This is the fallback, not a dead branch: it is what a
//    checkout without the folder gets, which is every machine but the one taking the screenshots.
//
// Either way the answer is a pure function of the fabricated path, the same on every launch and in
// every view, so the icon view's tile and the details strip's preview for one file cannot disagree
// and a capture retaken next month matches the one taken today. That is the same intent as the
// fixed RNG seed in DemoMode.cs and the fixed sizes and dates in DemoFileSystem.cs. The one thing
// that does move a capture is adding or removing a photograph, which reshuffles which fabricated
// row wears which picture.
namespace KillerShell.Services
{
    internal static class DemoImages
    {
        private static readonly object Gate = new();

        // Every fabricated path that gets a picture, mapped to its position in the fabricated
        // machine's own fixed walk. A row the listing draws an image icon for is exactly a row this
        // answers, so nothing is left in a broken half-state. The POSITION is what picks the
        // photograph, which is why this is a map and not a set.
        private static Dictionary<string, int>? _known;

        // The photographs, sorted by file name so the order cannot depend on how the file system
        // happened to enumerate the folder. Empty when the folder is not there. Built on the first
        // Render call, and Render is only ever reached in demo mode, so an ordinary run never goes
        // looking for it.
        private static List<string>? _photos;

        // What is taken as a photograph in that folder. The same set DemoFileSystem.cs treats as a
        // picture, so anything dropped in there is readable by exactly the rows that want one.
        private static readonly string[] PhotoExtensions =
            [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp"];

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
        /// or null when the path is not one of the fabricated machine's pictures. A photograph from
        /// the demo folder when there is one, otherwise drawn. Frozen, so the background threads the
        /// listing and the details strip decode on can hand it to the UI thread directly.
        /// </summary>
        internal static BitmapSource? Render(string fakePath, int px)
        {
            if (string.IsNullOrEmpty(fakePath) || px <= 0) return null;

            string key = fakePath + "|" + px;

            Dictionary<string, int> known;
            List<string> photos;
            lock (Gate)
            {
                _known ??= BuildKnown();
                _photos ??= BuildPhotos();
                known = _known;
                photos = _photos;
                if (Cache.TryGetValue(key, out var hit)) return hit;
            }

            if (!known.TryGetValue(fakePath, out int index)) return null;

            // Loaded and drawn OUTSIDE the lock: two threads racing on the same tile produce the
            // same pixels, because the picture is a function of the path, so the wasted work is one
            // tile and the alternative is every listing thread queueing behind whichever one is
            // working.
            //
            // Modulo, so a folder holding fewer photographs than the machine has picture rows still
            // fills every row rather than leaving the tail drawn and the head photographic - a grid
            // mixing the two reads as a bug in a screenshot.
            BitmapSource? img = photos.Count > 0
                ? ImageOrientation.Load(photos[index % photos.Count], px, ignoreColorProfile: false)
                : null;

            img ??= Draw(fakePath, px);

            lock (Gate) Cache[key] = img;
            return img;
        }

        private static Dictionary<string, int> BuildKnown()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (string p in DemoFs.ImagePaths)
                if (!map.ContainsKey(p)) map[p] = i++;
            return map;
        }

        /// <summary>
        /// The demo photo folder, code\Demo\KillerShell, found by walking up from the running exe
        /// (bin\Debug\net48 and the rest) and then across. Deliberately the same lookup KillerNotes'
        /// DemoMode.cs does for its own folder: one convention for where demo material lives, so a
        /// second app does not invent a second place to put it. Empty when it is not there, which is
        /// not an error - see the fallback at the top of this file.
        /// </summary>
        private static string PhotoDir()
        {
            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int up = 0; up < 6 && dir != null; up++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "Demo", "KillerShell");
                    if (Directory.Exists(candidate)) return candidate;

                    string sibling = Path.Combine(dir.FullName, "..", "Demo", "KillerShell");
                    if (Directory.Exists(sibling)) return Path.GetFullPath(sibling);
                }
            }
            catch { }
            return string.Empty;
        }

        private static List<string> BuildPhotos()
        {
            var found = new List<string>();
            try
            {
                string dir = PhotoDir();
                if (dir.Length == 0) return found;

                foreach (string file in Directory.GetFiles(dir))
                    foreach (string ext in PhotoExtensions)
                        if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(file);
                            break;
                        }

                // Sorted by NAME rather than left in enumeration order, which is what makes the
                // same folder produce the same assignment on a second machine.
                found.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b),
                                                    StringComparison.OrdinalIgnoreCase));
            }
            catch { found.Clear(); }
            return found;
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
