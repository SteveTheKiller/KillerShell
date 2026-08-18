using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using KillerShell.Tools;

namespace KillerShell.Services
{
    internal sealed class StorageHtmlExporter
    {
        internal sealed class Palette
        {
            internal string Key = "", Label = "", Bg = "#1c1c1c", Surface = "#333333",
                Pane = "#3a3a3a", Accent = "#50aee8", Text = "#e0e0e0",
                Muted = "#a0a0a0", Border = "#3f3f3f", Hover = "#404040";
        }

        private static readonly (string Key, string Label, string File)[] ThemeFiles =
        {
            ("dark", "Dark", "Dark"), ("light", "Light", "Light"),
            ("black", "Black", "Black"), ("98se", "98SE", "98SE"),
            ("blood", "Blood", "Blood"), ("greed", "Greed", "Greed"),
            ("cyanotic", "Cyanotic", "Cyanotic"), ("ectoplasm", "Ectoplasm", "Ectoplasm"),
            ("decay", "Decay", "Decay"), ("mourning", "Mourning", "Mourning"),
            ("sepulchre", "Sepulchre", "Sepulchre"), ("delirium", "Delirium", "Delirium"),
            ("malaise", "Malaise", "Malaise")
        };

        internal void Export(string outputPath, StorageReport report)
        {
            var palettes = LoadPalettes();
            string current = ThemeManager.Current == Theme.SE98
                ? "98se" : ThemeManager.Current.ToString().ToLowerInvariant();
            string accent = BrushHex(Application.Current?.TryFindResource("PrimaryBrush"), "#50aee8");
            var all = Descendants(report.Root).ToList();
            var folders = all.Where(n => n.IsDirectory && !ReferenceEquals(n, report.Root))
                .OrderByDescending(n => n.Size).Take(50).ToList();
            var files = all.Where(n => !n.IsDirectory).OrderByDescending(n => n.Size).Take(50).ToList();
            var sb = new StringBuilder();

            sb.Append("<!doctype html><html lang='en' class='theme-").Append(current)
              .Append("'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>")
              .Append("<title>KillerShell Storage Report</title><style>");
            foreach (var p in palettes)
                sb.Append("html.theme-").Append(p.Key).Append("{--bg:").Append(p.Bg)
                  .Append(";--surface:").Append(p.Surface).Append(";--pane:").Append(p.Pane)
                  .Append(";--accent:").Append(p.Accent).Append(";--text:").Append(p.Text)
                  .Append(";--muted:").Append(p.Muted).Append(";--border:").Append(p.Border)
                  .Append(";--hover:").Append(p.Hover).Append("}");
            sb.Append("*{box-sizing:border-box}body{margin:0;padding:24px;background:var(--bg);color:var(--text);font:14px 'Segoe UI',sans-serif}.wrap{max-width:1280px;margin:auto}h1,h2{font-family:Consolas,monospace}h1{margin:0}.accent{color:var(--accent)}.top{display:flex;justify-content:space-between;gap:20px;flex-wrap:wrap}.switch{display:flex;gap:6px;flex-wrap:wrap;max-width:340px}.switch button{width:19px;height:19px;border-radius:50%;border:2px solid var(--border);cursor:pointer}.switch button.active{border-color:var(--text)}.meta{color:var(--muted);line-height:1.8}.cards{display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin:18px 0}.card,.map,table{background:var(--pane);border:1px solid var(--border)}.card{padding:13px}.card b{display:block;font:20px Consolas,monospace;color:var(--accent)}.map{padding:8px;overflow:hidden}svg{display:block;width:100%;height:auto}rect{stroke:var(--bg);stroke-width:1}text{fill:#fff;font:11px Consolas,monospace;pointer-events:none;text-shadow:0 1px 2px #000}.tables{display:grid;grid-template-columns:1fr 1fr;gap:18px}table{border-collapse:collapse;width:100%;table-layout:fixed}th,td{padding:8px 10px;border-bottom:1px solid var(--border);text-align:left}th{background:var(--surface);color:var(--muted)}tr:hover td{background:var(--hover)}td.path{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}td.num{width:110px;text-align:right;font-family:Consolas,monospace}.foot{color:var(--muted);font-size:11px;margin-top:18px}@media(max-width:800px){body{padding:12px}.cards,.tables{grid-template-columns:1fr}}</style></head><body><div class='wrap'>");
            sb.Append("<div class='top'><div><h1>Killer<span class='accent'>Shell</span> Storage</h1><div class='meta'>Generated ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("<br>View: ").Append(E(report.ViewRoot))
              .Append("<br>Scan root: ").Append(E(report.ScanRoot)).Append("</div></div><div id='themes' class='switch'></div></div>");
            sb.Append("<div class='cards'><div class='card'><span>Total shown</span><b>").Append(Size(report.TotalSize))
              .Append("</b></div><div class='card'><span>Depth filter</span><b>").Append(report.DepthLimit == 0 ? "All" : report.DepthLimit.ToString())
              .Append("</b></div><div class='card'><span>Minimum size</span><b>").Append(report.MinimumSize == 0 ? "All" : Size(report.MinimumSize)).Append("</b></div></div>");
            sb.Append("<h2>Treemap</h2><div class='map'><svg viewBox='0 0 1200 500' role='img' aria-label='Storage treemap'>");
            DrawChildren(sb, report.Root, 0, 0, 1200, 500, 1, report.DepthLimit, report.TotalSize);
            sb.Append("</svg></div><div class='tables'>");
            AddTable(sb, "Biggest folders", folders, report.TotalSize);
            AddTable(sb, "Biggest files", files, report.TotalSize);
            sb.Append("</div><div class='foot'>Generated by KillerShell</div></div><script>var T=[");
            sb.Append(string.Join(",", palettes.Select(p => "['" + p.Key + "','" + p.Label + "','" + p.Pane + "']")));
            sb.Append("];var s=document.getElementById('themes');function setTheme(k){document.documentElement.className='theme-'+k;Array.prototype.forEach.call(s.children,function(b){b.className=b.dataset.k===k?'active':''})}T.forEach(function(t){var b=document.createElement('button');b.title=t[1];b.dataset.k=t[0];b.style.background=t[2];b.onclick=function(){setTheme(t[0])};s.appendChild(b)});setTheme('")
              .Append(current).Append("');document.documentElement.style.setProperty('--accent','").Append(accent).Append("');</script></body></html>");
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        private static IEnumerable<StorageReportNode> Descendants(StorageReportNode n)
        {
            yield return n;
            foreach (var child in n.Children)
                foreach (var item in Descendants(child)) yield return item;
        }

        private static void AddTable(StringBuilder sb, string title, IList<StorageReportNode> nodes, long total)
        {
            sb.Append("<section><h2>").Append(title).Append("</h2><table><thead><tr><th>Path</th><th class='num'>Size</th><th class='num'>Percent</th></tr></thead><tbody>");
            foreach (var n in nodes)
                sb.Append("<tr><td class='path' title='").Append(E(n.Path)).Append("'>").Append(E(n.Path))
                  .Append("</td><td class='num'>").Append(Size(n.Size)).Append("</td><td class='num'>")
                  .Append(total > 0 ? (100.0 * n.Size / total).ToString("0.0", CultureInfo.InvariantCulture) : "0").Append("%</td></tr>");
            sb.Append("</tbody></table></section>");
        }

        private static readonly string[] MapColors = { "#276fbf", "#d14b52", "#2f9e62", "#c1842b", "#7957b8", "#208f9f", "#a64d79", "#687a35" };
        private static void DrawChildren(StringBuilder sb, StorageReportNode node, double x, double y, double w, double h, int depth, int limit, long total)
        {
            if (node.Children.Count == 0 || (limit > 0 && depth > limit)) return;
            double at = 0; bool horizontal = depth % 2 == 1; int i = 0;
            foreach (var child in node.Children.Where(c => c.Size > 0))
            {
                double ratio = node.Size > 0 ? (double)child.Size / node.Size : 0;
                double cw = horizontal ? w * ratio : w, ch = horizontal ? h : h * ratio;
                double cx = horizontal ? x + at : x, cy = horizontal ? y : y + at;
                at += horizontal ? cw : ch;
                if (cw < 1 || ch < 1) continue;
                string color = MapColors[i++ % MapColors.Length];
                sb.Append("<g><title>").Append(E(child.Path)).Append(" - ").Append(Size(child.Size)).Append(" - ")
                  .Append(total > 0 ? (100.0 * child.Size / total).ToString("0.0", CultureInfo.InvariantCulture) : "0")
                  .Append("%</title><rect x='").Append(N(cx)).Append("' y='").Append(N(cy)).Append("' width='").Append(N(cw)).Append("' height='").Append(N(ch)).Append("' fill='").Append(color).Append("'/>");
                if (cw > 85 && ch > 18) sb.Append("<text x='").Append(N(cx + 4)).Append("' y='").Append(N(cy + 14)).Append("'>").Append(E(child.Name)).Append("</text>");
                sb.Append("</g>");
                if (child.IsDirectory) DrawChildren(sb, child, cx + 1, cy + 18, Math.Max(0, cw - 2), Math.Max(0, ch - 19), depth + 1, limit, total);
            }
        }

        internal static List<Palette> LoadPalettes()
        {
            var result = new List<Palette>();
            foreach (var item in ThemeFiles)
            {
                try
                {
                    var d = (ResourceDictionary)Application.LoadComponent(new Uri("/KillerShell;component/Themes/" + item.File + ".xaml", UriKind.Relative));
                    result.Add(new Palette { Key=item.Key, Label=item.Label, Bg=Hex(d,"BackgroundBrush"), Surface=Hex(d,"SurfaceBrush"), Pane=Hex(d,"PaneBrush"), Accent=Hex(d,"PrimaryBrush"), Text=Hex(d,"TextBrush"), Muted=Hex(d,"MutedTextBrush"), Border=Hex(d,"PaneBorderBrush"), Hover=Hex(d,"RowHoverBrush") });
                }
                catch { result.Add(new Palette { Key=item.Key, Label=item.Label }); }
            }
            return result;
        }

        private static string Hex(ResourceDictionary d, string key) => BrushHex(d[key], "#808080");
        private static string BrushHex(object? value, string fallback) => value is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : fallback;
        private static string N(double n) => n.ToString("0.##", CultureInfo.InvariantCulture);
        private static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        private static string Size(long b) { const double k=1024,m=k*1024,g=m*1024,t=g*1024; return b>=t?$"{b/t:0.00} TB":b>=g?$"{b/g:0.00} GB":b>=m?$"{b/m:0.0} MB":b>=k?$"{b/k:0.0} KB":$"{b} B"; }
    }
}
