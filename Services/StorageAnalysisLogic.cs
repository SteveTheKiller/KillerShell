using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KillerShell.Services
{
    internal static class StorageAnalysisLogic
    {
        private static readonly Dictionary<string, string> ExtensionCategories = BuildCategories();

        internal static string ShortSize(long bytes)
        {
            const long mb = 1L << 20;
            const long gb = 1L << 30;
            if (bytes >= gb) return (bytes / gb).ToString(CultureInfo.InvariantCulture) + "G";
            if (bytes >= mb) return (bytes / mb).ToString(CultureInfo.InvariantCulture) + "M";
            return (bytes / 1024).ToString(CultureInfo.InvariantCulture) + "K";
        }

        internal static string FormatSize(long bytes)
        {
            const double kb = 1024;
            const double mb = kb * 1024;
            const double gb = mb * 1024;
            const double tb = gb * 1024;
            if (bytes >= tb) return (bytes / tb).ToString("0.00", CultureInfo.InvariantCulture) + " TB";
            if (bytes >= gb) return (bytes / gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= mb) return (bytes / mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= kb) return (bytes / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        internal static string CategoryFor(string fileName)
        {
            int dot = fileName.LastIndexOf('.');
            string extension = dot >= 0 && dot < fileName.Length - 1 ? fileName[(dot + 1)..] : string.Empty;
            return ExtensionCategories.TryGetValue(extension, out string? category) ? category : "oth";
        }

        internal static Color CategoryColor(string category) => category switch
        {
            "img" => Color.FromRgb(0xF2, 0x22, 0xFF),
            "vid" => Color.FromRgb(0x8C, 0x1E, 0xFF),
            "aud" => Color.FromRgb(0x00, 0xC8, 0xC3),
            "doc" => Color.FromRgb(0xFF, 0xD3, 0x19),
            "arc" => Color.FromRgb(0xFF, 0x8C, 0x00),
            "code" => Color.FromRgb(0x39, 0xC8, 0x14),
            "sys" => Color.FromRgb(0xC8, 0x3C, 0x38),
            "data" => Color.FromRgb(0x2D, 0x8C, 0xFF),
            "pkg" => Color.FromRgb(0xFF, 0x5C, 0x8A),
            "font" => Color.FromRgb(0xB9, 0x8A, 0xFF),
            "cfg" => Color.FromRgb(0x00, 0xA8, 0x78),
            _ => Color.FromRgb(0x6E, 0x6E, 0x6E),
        };

        internal static Rect[] Squarify(IReadOnlyList<long> sizes, long total, Rect bounds)
        {
            var result = new Rect[sizes.Count];
            double x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height;
            double scale = width * height / Math.Max(1, total);
            int index = 0;
            while (index < sizes.Count)
            {
                bool horizontal = width < height;
                double side = horizontal ? width : height;
                if (side < 1) break;
                int start = index, end = index;
                double rowArea = 0, rowMax = 0, rowMin = double.MaxValue, worst = double.MaxValue;
                while (end < sizes.Count)
                {
                    double area = Math.Max(0.0001, sizes[end] * scale);
                    double nextArea = rowArea + area;
                    double nextMax = Math.Max(rowMax, area), nextMin = Math.Min(rowMin, area);
                    double nextWorst = Math.Max(side * side * nextMax / (nextArea * nextArea),
                        nextArea * nextArea / (side * side * nextMin));
                    if (nextWorst > worst && end > start) break;
                    rowArea = nextArea;
                    rowMax = nextMax;
                    rowMin = nextMin;
                    worst = nextWorst;
                    end++;
                }
                double thickness = rowArea / side;
                double along = horizontal ? x : y;
                for (int item = start; item < end; item++)
                {
                    double area = Math.Max(0.0001, sizes[item] * scale);
                    double length = area / Math.Max(0.0001, thickness);
                    result[item] = horizontal
                        ? new Rect(along, y, length, thickness)
                        : new Rect(x, along, thickness, length);
                    along += length;
                }
                if (horizontal) { y += thickness; height -= thickness; }
                else { x += thickness; width -= thickness; }
                index = end;
                if (width < 0.5 || height < 0.5) break;
            }
            return result;
        }

        private static Dictionary<string, string> BuildCategories()
        {
            var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string category, string extensions)
            {
                foreach (string extension in extensions.Split(' ')) categories[extension] = category;
            }
            Add("img", "png jpg jpeg gif bmp webp ico svg tif tiff raw heic heif avif jxl psd xcf kra");
            Add("vid", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts mts m2ts vob 3gp ogv");
            Add("aud", "mp3 wav flac ogg m4a wma aac opus mid midi aif aiff alac ape");
            Add("doc", "pdf doc docx xls xlsx ppt pptx odt ods odp txt md rtf csv epub mobi azw azw3 one msg eml tex");
            Add("arc", "zip rar 7z tar gz bz2 xz zst tgz tbz2 txz iso cab wim vhd vhdx vdi vmdk qcow qcow2 img");
            Add("code", "cs fs fsx vb js jsx ts tsx py cpp c h hpp html css xaml json xml yml yaml sql ps1 psm1 sh bat cmd java rs go rb lua php swift kt kts dart scala vue svelte razor cshtml");
            Add("sys", "exe dll sys msi msp msix appx ocx drv efi mui winmd pdb lib obj lnk scr cpl");
            Add("data", "db db3 sqlite sqlite3 mdb accdb dbf dat bin blob cache index edb ldf mdf ndf log evtx etl trace dmp dump bak tmp temp ost pst");
            Add("pkg", "archive pak vpk cpk bundle bundles asset assets resource resources unity3d uasset uexp ubulk utoc ucas pck");
            Add("font", "ttf otf woff woff2 eot fon fnt");
            Add("cfg", "ini cfg conf config toml properties reg inf manifest lock");
            return categories;
        }

        internal static long Aggregate<T>(T node, Func<T, IList<T>?> children,
            Func<T, long> readSize, Action<T, long> writeSize)
        {
            IList<T>? descendants = children(node);
            if (descendants == null) return readSize(node);
            long total = 0;
            foreach (T child in descendants)
                total += Aggregate(child, children, readSize, writeSize);
            writeSize(node, total);
            var sorted = new List<T>(descendants);
            sorted.Sort((left, right) => readSize(right).CompareTo(readSize(left)));
            descendants.Clear();
            foreach (T child in sorted) descendants.Add(child);
            return total;
        }
    }
}
