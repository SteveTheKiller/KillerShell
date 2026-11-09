using System.Windows;
using System.Windows.Media;

// The Performance tab's graph colors. Each metric owns one color, so a trace or a headline number
// can be read by color alone: CPU green, memory blue, disk amber reads and red writes, network
// magenta sends and yellow receives, GPU cyan. The second series of one device (cached memory,
// shared GPU memory) is a lighter shade of that device's color.
//
// These are fixed per metric rather than mirrored from the accent, because an accent can be any
// of these hues and would then collide with another graph. The dark set is picked for the dark tile
// surfaces of twelve themes (98SE's black readouts included) and clears 3:1 contrast on the
// lightest of them, Mourning. A theme whose tiles are light (Light) gets the same hues darkened.
// The choice measures the tile surface instead of naming a theme, and the theme files stay as the
// family kit ships them. PerformanceGraphBrushesTests holds every theme file to both rules.
namespace KillerShell.Tools
{
    internal static class PerformanceGraphBrushes
    {
        internal const string Cpu = "MonitorCpuBrush";
        internal const string Ram = "MonitorRamBrush";
        internal const string RamCached = "MonitorRamCachedBrush";
        internal const string DiskRead = "MonitorDiskReadBrush";
        internal const string DiskWrite = "MonitorDiskWriteBrush";
        internal const string NetSend = "MonitorNetSendBrush";
        internal const string NetReceive = "MonitorNetReceiveBrush";
        internal const string Gpu = "MonitorGpuBrush";
        internal const string GpuShared = "MonitorGpuSharedBrush";

        internal static readonly string[] Keys = [Cpu, Ram, RamCached, DiskRead, DiskWrite, NetSend, NetReceive, Gpu, GpuShared];

        private static readonly (string Key, Color OnDark, Color OnLight)[] Defaults =
        [
            (Cpu,        Color.FromRgb(0x4A, 0xDE, 0x6A), Color.FromRgb(0x1A, 0x7F, 0x37)),
            (Ram,        Color.FromRgb(0x6A, 0xA8, 0xF2), Color.FromRgb(0x1D, 0x5F, 0xB0)),
            (RamCached,  Color.FromRgb(0xA9, 0xCC, 0xF7), Color.FromRgb(0x4A, 0x73, 0xAB)),
            (DiskRead,   Color.FromRgb(0xF5, 0xA6, 0x23), Color.FromRgb(0x98, 0x5A, 0x00)),
            (DiskWrite,  Color.FromRgb(0xFF, 0x70, 0x70), Color.FromRgb(0xC0, 0x1C, 0x28)),
            (NetSend,    Color.FromRgb(0xF5, 0x7E, 0xE8), Color.FromRgb(0xA0, 0x17, 0x9A)),
            (NetReceive, Color.FromRgb(0xF5, 0xE0, 0x4A), Color.FromRgb(0x6E, 0x60, 0x00)),
            (Gpu,        Color.FromRgb(0x2B, 0xD9, 0xE8), Color.FromRgb(0x00, 0x70, 0x7C)),
            (GpuShared,  Color.FromRgb(0x9A, 0xEE, 0xF5), Color.FromRgb(0x2E, 0x7C, 0x86)),
        ];

        /// <summary>Fill in every graph color the theme does not state itself.</summary>
        internal static void Complete(ResourceDictionary theme)
        {
            bool lightTiles = Luminance(TileSurface(theme)) > 0.4;
            foreach (var (key, onDark, onLight) in Defaults)
            {
                if (theme.Contains(key)) continue;
                var brush = new SolidColorBrush(lightTiles ? onLight : onDark);
                brush.Freeze();
                theme[key] = brush;
            }
        }

        // The surface the graphs are drawn on: the theme's own cell color when it states one
        // (98SE's black readouts), otherwise the list pane the cards take, otherwise the pane.
        // A gradient counts as the average of its stops.
        private static Color TileSurface(ResourceDictionary theme)
        {
            foreach (string key in new[] { "MonitorCellBrush", "ListPaneBrush", "PaneBrush" })
            {
                if (!theme.Contains(key)) continue;
                if (theme[key] is SolidColorBrush solid) return solid.Color;
                if (theme[key] is GradientBrush gradient && gradient.GradientStops.Count > 0)
                {
                    double r = 0, g = 0, b = 0;
                    foreach (var stop in gradient.GradientStops) { r += stop.Color.R; g += stop.Color.G; b += stop.Color.B; }
                    int n = gradient.GradientStops.Count;
                    return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
                }
            }
            return Colors.Black;
        }

        private static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
    }
}
