using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using KillerShell.Models;

namespace KillerShell.Services
{
    // Styled, interactive HTML report - mirrors the KillerScan exporter: an embedded
    // six-theme switcher + accent picker and click-to-sort columns. One compact table
    // row per file; content hits expand inline.
    //
    // The report opens in the theme, accent, and LANGUAGE the app was in at export
    // time: strings are resolved through the app's locale resources while writing,
    // so the file carries exactly one language and needs no i18n payload.
    public class HtmlExporter
    {
        // Optional brand assets; the report works fully without them (text wordmark
        // + solid colors fall back), so these can 404 safely.
        private const string AssetBase = "https://killershell.net/assets/";
        private const string SiteUrl   = "https://killershell.net";

        /// <param name="browsing">
        /// True when the tab is a folder listing rather than a search. A listing has no query
        /// and no matches, so "Searched X for everything - 0 matches" is wrong twice over. It
        /// cannot be inferred from an empty <paramref name="terms"/>: a search with only
        /// FILTERS ("every .pdf over 100 MB") also has no terms and is still a search.
        /// </param>
        public void Export(string outputPath,
                           IList<SearchResult> results,
                           IList<SearchTerm>   terms,
                           string              rootPath,
                           bool                browsing = false)
        {
            string current = Services.ThemeManager.Current.ToString().ToLowerInvariant();
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            // The live PrimaryBrush IS the current accent (family overlay included),
            // so the report defaults to the exact color on screen at export time.
            string accentHex = "#50AEE8";
            if (Application.Current?.TryFindResource("PrimaryBrush") is SolidColorBrush pb)
                accentHex = $"#{pb.Color.R:X2}{pb.Color.G:X2}{pb.Color.B:X2}";

            string lang = Services.LocaleManager.Current switch
            {
                Services.Locale.Es   => "es",
                Services.Locale.De   => "de",
                Services.Locale.Fr   => "fr",
                Services.Locale.TrTR => "tr",
                Services.Locale.ZhCN => "zh-CN",
                Services.Locale.ZhTW => "zh-TW",
                Services.Locale.Bn   => "bn",
                _                    => "en",
            };

            var sb = new StringBuilder();

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine($"<html lang='{lang}' class='theme-{current}'><head><meta charset='utf-8'/>");
            sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'/>");
            sb.AppendLine("<title>KillerShell Report</title>");
            sb.AppendLine("<style>");

            // Per-theme variable blocks (KillerShell's brand accent is blue on the neutrals).
            foreach (var t in ReportThemes)
            {
                var b = new StringBuilder();
                b.Append("html.theme-").Append(t.Key).Append('{')
                 .Append("--bg:").Append(t.Bg).Append(';')
                 .Append("--surface:").Append(t.Surface).Append(';')
                 .Append("--pane:").Append(t.Pane).Append(';')
                 .Append("--accent:").Append(t.Accent).Append(';')
                 .Append("--text:").Append(t.Text).Append(';')
                 .Append("--muted:").Append(t.Muted).Append(';')
                 .Append("--border:").Append(t.Border).Append(';')
                 .Append("--hover:").Append(t.Hover).Append(';')
                 .Append('}');
                sb.AppendLine(b.ToString());
            }

            sb.AppendLine("*{box-sizing:border-box}");
            sb.AppendLine("body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,'Segoe UI',Roboto,sans-serif;padding:24px;line-height:1.5}");
            sb.AppendLine(".wrap{max-width:1250px;margin:0 auto}");
            sb.AppendLine(".topbar{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;margin-bottom:8px}");
            sb.AppendLine(".brand{display:flex;align-items:center;gap:10px;min-height:38px}");
            sb.AppendLine(".logo{height:36px;display:block}");
            sb.AppendLine(".wordmark{font-family:Consolas,'Courier New',monospace;font-size:27px;font-weight:700;letter-spacing:.5px}");
            sb.AppendLine(".wordmark .k{color:var(--text)}.wordmark .s{color:var(--accent)}");
            sb.AppendLine(".switchers{display:flex;flex-direction:column;gap:6px;align-items:flex-end}");
            sb.AppendLine(".swrow{display:flex;align-items:center;gap:8px}");
            sb.AppendLine(".swlabel{color:var(--muted);font-size:10px;font-family:Consolas,monospace;letter-spacing:.5px;text-transform:uppercase}");
            sb.AppendLine(".themesw{display:flex;gap:7px;align-items:center}");
            sb.AppendLine(".themesw button{width:18px;height:18px;border-radius:50%;border:2px solid var(--border);cursor:pointer;padding:0;outline:none;transition:transform .1s}");
            sb.AppendLine(".themesw button:hover{transform:scale(1.15)}.themesw button.active{border-color:var(--text)}");
            sb.AppendLine(".meta{color:var(--muted);font-size:13px;margin:0 0 6px}.meta b{color:var(--text)}");
            sb.AppendLine(".query{color:var(--muted);font-size:14px;margin:0 0 18px}.query b{color:var(--accent);font-weight:600}");
            // Grain goes on a ::before overlay at the app's GrainOpacity, NOT straight into
            // the background shorthand - a CSS background-image has no opacity of its own, so
            // the tile was compositing at FULL strength and burying the table in noise.
            sb.AppendLine(".tablewrap{position:relative;overflow-x:auto;border:1px solid var(--border);border-radius:8px;background:var(--pane);box-shadow:0 10px 30px rgba(0,0,0,.45)}");
            sb.AppendLine($".tablewrap::before{{content:'';position:absolute;inset:0;background:url('{AssetBase}killershell-grain.png');opacity:.24;pointer-events:none;z-index:0}}");
            sb.AppendLine("table{position:relative;z-index:1;border-collapse:collapse;width:100%;min-width:860px}");
            sb.AppendLine("th{background:var(--surface);color:var(--muted);text-align:left;padding:10px 14px;font-size:12px;font-weight:600;font-family:Consolas,monospace;letter-spacing:.3px;position:sticky;top:0;white-space:nowrap;cursor:pointer;user-select:none}");
            sb.AppendLine("th:hover{color:var(--text)}th .arrow{display:inline-block;width:10px;font-size:10px;opacity:.7}");
            sb.AppendLine("td{border-top:1px solid var(--border);padding:7px 14px;font-size:13px;vertical-align:top}");
            sb.AppendLine("td.name{font-family:Consolas,monospace;font-weight:600;white-space:nowrap}");
            sb.AppendLine("td.dir{font-family:Consolas,monospace;font-size:12px;color:var(--muted);word-break:break-all}");
            sb.AppendLine("td.num{font-family:Consolas,monospace;white-space:nowrap;text-align:right}");
            sb.AppendLine("td.mod{font-family:Consolas,monospace;white-space:nowrap}");
            sb.AppendLine("td.found{font-size:12px;white-space:nowrap;color:var(--accent)}");
            sb.AppendLine("tbody tr:hover td{background:var(--hover)}");
            sb.AppendLine("details summary{cursor:pointer;outline:none}");
            sb.AppendLine(".lines{margin:6px 0 2px;font-family:Consolas,monospace;font-size:11px;color:var(--muted);white-space:pre-wrap;word-break:break-all;max-width:520px}");
            sb.AppendLine(".lines b{color:var(--text);font-weight:600}");
            sb.AppendLine(".footer{color:var(--muted);font-size:11px;margin-top:18px;opacity:.85}");
            sb.AppendLine(".footer a{color:var(--accent);text-decoration:none}.footer a:hover{text-decoration:underline}");
            sb.AppendLine("@media(max-width:640px){body{padding:12px}.wordmark{font-size:22px}th,td{padding:8px 10px;font-size:12px}}");
            sb.AppendLine("</style></head><body><div class='wrap'>");

            // Header: hosted wordmark if available, Consolas text fallback otherwise.
            sb.AppendLine("<div class='topbar'><div class='brand'>");
            sb.AppendLine($"<img class='logo' src='{AssetBase}killershell-wordmark.png' alt='' onload=\"document.getElementById('wm').style.display='none'\" onerror=\"this.style.display='none'\"/>");
            sb.AppendLine("<span id='wm' class='wordmark'><span class='k'>Killer</span><span class='s'>Shell</span></span>");
            sb.AppendLine("</div><div class='switchers'>");
            sb.AppendLine($"<div class='swrow'><span class='swlabel'>{L("Str_Lbl_Theme", "theme")}</span><div class='themesw' id='themesw'></div></div>");
            sb.AppendLine($"<div class='swrow'><span class='swlabel'>{L("Str_Lbl_Accent", "accent")}</span><div class='themesw' id='accentsw'></div></div>");
            sb.AppendLine("</div></div>");

            int totalMatches = results.Sum(r => r.TotalMatchCount);
            // A folder listing counts ITEMS and has no match count to report; only a search
            // does. Reporting "0 matches" over a full listing read as a broken export.
            string meta = browsing
                ? string.Format(L("Str_Rpt_MetaList", "Generated {0} · {1} items"),
                    $"<b>{ts}</b>", $"<b>{results.Count:N0}</b>")
                : string.Format(L("Str_Rpt_Meta", "Generated {0} · {1} files · {2} matches"),
                    $"<b>{ts}</b>", $"<b>{results.Count:N0}</b>", $"<b>{totalMatches:N0}</b>");
            sb.AppendLine($"<p class='meta'>{meta}</p>");

            // Plain-language description of what was searched, instead of raw term chips.
            var termBits = terms.Where(t => !string.IsNullOrWhiteSpace(t.Pattern))
                .Select(t => string.Format(
                    t.Mode == SearchTerm.SearchMode.Content
                        ? L("Str_Rpt_TermContent", "file contents containing {0}")
                        : L("Str_Rpt_TermName",    "file names containing {0}"),
                    $"<b>&quot;{Esc(t.Pattern.Trim())}&quot;</b>"))
                .ToList();
            string or = L("Str_Rpt_Or", "or");
            string what = termBits.Count switch
            {
                0 => L("Str_Rpt_Everything", "everything"),
                1 => termBits[0],
                _ => string.Join(", ", termBits.Take(termBits.Count - 1)) + $" {or} " + termBits[^1],
            };
            sb.AppendLine($"<p class='query'>{(browsing
                ? string.Format(L("Str_Rpt_Listing", "Listing of {0}."), $"<b>{Esc(rootPath)}</b>")
                : string.Format(L("Str_Rpt_Query", "Searched {0} for {1}."), $"<b>{Esc(rootPath)}</b>", what))}</p>");

            sb.AppendLine("<div class='tablewrap'><table id='tbl'><thead><tr>");
            sb.AppendLine($"<th data-type='text'>{L("Str_Col_Name", "name")} <span class='arrow'></span></th>");
            sb.AppendLine($"<th data-type='text'>{L("Str_Col_Folder", "location")} <span class='arrow'></span></th>");
            sb.AppendLine($"<th data-type='num'>{L("Str_Col_Size", "size")} <span class='arrow'></span></th>");
            sb.AppendLine($"<th data-type='num'>{L("Str_Col_Modified", "modified")} <span class='arrow'></span></th>");
            sb.AppendLine($"<th data-type='text'>{L("Str_Rpt_Found", "found by")} <span class='arrow'></span></th>");
            sb.AppendLine("</tr></thead><tbody>");

            foreach (var r in results)
            {
                // The engine (and demo mode) already statted every result - use the stored
                // values instead of re-hitting the disk per row at export time.
                long size = r.SizeBytes;
                DateTime mod = r.Modified;

                sb.Append("<tr>")
                  .Append($"<td class='name'>{Esc(r.FileName)}</td>")
                  .Append($"<td class='dir'>{Esc(r.Directory)}</td>")
                  .Append($"<td class='num' data-sort='{size}'>{FormatSize(size)}</td>")
                  .Append($"<td class='mod' data-sort='{(mod == DateTime.MinValue ? "0" : mod.ToString("yyyyMMddHHmmss"))}'>{(mod == DateTime.MinValue ? "" : mod.ToString("yyyy-MM-dd HH:mm"))}</td>")
                  .Append($"<td class='found'>{FoundCell(r)}</td>")
                  .AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table></div>");

            string genBy = string.Format(L("Str_Rpt_GenBy", "Generated by {0}"),
                $"<a href='{SiteUrl}' target='_blank' rel='noopener'>KillerShell</a>");
            sb.AppendLine($"<p class='footer'>{genBy} &middot; &copy; 2026 Steve the Killer</p>");
            sb.AppendLine("</div>");

            // Interactivity: theme + accent switchers and click-to-sort columns. The report
            // always opens in the theme/accent it was EXPORTED in (no persisted override).
            sb.AppendLine("<script>");
            sb.AppendLine("var THEMES=[['dark','Dark','#3a3a3a'],['light','Light','#e8e8e8'],['black','Black','#000000'],['blood','Blood','#4a1f20'],['greed','Greed','#0a5234'],['cyanotic','Cyanotic','#0a4a6e']];");
            sb.AppendLine("var sw=document.getElementById('themesw');");
            sb.AppendLine("function setTheme(t){document.documentElement.className='theme-'+t;var k=sw.children;for(var i=0;i<k.length;i++)k[i].className=(k[i].getAttribute('data-t')===t)?'active':'';}");
            sb.AppendLine("THEMES.forEach(function(a){var b=document.createElement('button');b.title=a[1];b.setAttribute('data-t',a[0]);b.style.background=a[2];b.onclick=function(){setTheme(a[0])};sw.appendChild(b);});");
            sb.AppendLine($"setTheme('{current}');");
            sb.AppendLine("var ACCENTS=[['#DD504B','Red'],['#E8962C','Orange'],['#1EA54C','Green'],['#1FB8A8','Teal'],['#50AEE8','Blue'],['#B982E3','Purple']];");
            sb.AppendLine("var asw=document.getElementById('accentsw');");
            sb.AppendLine("function setAccent(c){if(c){document.documentElement.style.setProperty('--accent',c);}var k=asw.children;for(var i=0;i<k.length;i++)k[i].className=(k[i].getAttribute('data-c').toLowerCase()===String(c).toLowerCase())?'active':'';}");
            sb.AppendLine("ACCENTS.forEach(function(a){var b=document.createElement('button');b.title=a[1];b.setAttribute('data-c',a[0]);b.style.background=a[0];b.onclick=function(){setAccent(a[0])};asw.appendChild(b);});");
            sb.AppendLine($"setAccent('{accentHex}');");
            sb.AppendLine("var tbl=document.getElementById('tbl'),tb=tbl.tBodies[0],sortCol=-1,sortDir=1;");
            sb.AppendLine("var ths=tbl.tHead.rows[0].cells;");
            sb.AppendLine("for(var c=0;c<ths.length;c++){(function(i){ths[i].onclick=function(){");
            sb.AppendLine("sortDir=(sortCol===i)?-sortDir:1;sortCol=i;var num=ths[i].getAttribute('data-type')==='num';");
            sb.AppendLine("var rows=Array.prototype.slice.call(tb.rows);rows.sort(function(a,b){var x,y;if(num){x=parseFloat(a.cells[i].getAttribute('data-sort'))||0;y=parseFloat(b.cells[i].getAttribute('data-sort'))||0;}else{x=a.cells[i].innerText.toLowerCase();y=b.cells[i].innerText.toLowerCase();}return (x>y?1:x<y?-1:0)*sortDir;});");
            sb.AppendLine("for(var r=0;r<rows.length;r++)tb.appendChild(rows[r]);");
            sb.AppendLine("for(var h=0;h<ths.length;h++)ths[h].getElementsByClassName('arrow')[0].textContent='';");
            sb.AppendLine("ths[i].getElementsByClassName('arrow')[0].textContent=sortDir>0?'\\u25B2':'\\u25BC';};})(c);}");
            sb.AppendLine("</script>");
            sb.AppendLine("</body></html>");

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        // Resolve a UI string from the app's live locale resources (falls back to English).
        private static string L(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        // "name" for a filename hit; "content (12)" expands to the first matched lines.
        private static string FoundCell(SearchResult r)
        {
            var bits = new List<string>();
            foreach (var m in r.Matches)
            {
                if (m.Lines.Count == 0)
                {
                    bits.Add(Esc(m.Term.ModeName));
                    continue;
                }
                var inner = new StringBuilder();
                foreach (var l in m.Lines.Take(20))
                    inner.Append("<b>").Append(l.LineNumber).Append(":</b> ").Append(Esc(l.LineText)).Append('\n');
                if (m.Lines.Count > 20)
                    inner.Append(Esc(string.Format(L("Str_Rpt_More", "... +{0} more"), m.Lines.Count - 20))).Append('\n');
                bits.Add($"<details><summary>{Esc(m.Term.ModeName)} ({m.Lines.Count})</summary><div class='lines'>{inner}</div></details>");
            }
            return string.Join(" ", bits);
        }

        private static string FormatSize(long b)
        {
            if (b <= 0) return "";
            if (b < 1024) return b + " B";
            double kb = b / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            return (mb / 1024.0).ToString("0.00") + " GB";
        }

        // ---- Report palette data (the in-report switcher embeds all six themes) ----
        private readonly struct ReportTheme(string key, string bg, string surface, string pane, string accent,
                                            string text, string muted, string border, string hover)
        {
            public readonly string Key = key, Bg = bg, Surface = surface, Pane = pane, Accent = accent,
                                   Text = text, Muted = muted, Border = border, Hover = hover;
        }

        private static readonly ReportTheme[] ReportThemes =
        [
            new("dark",     "#1c1c1c", "#333333", "#3a3a3a", "#50AEE8", "#e0e0e0", "#a0a0a0", "#2e2e2e", "#404040"),
            new("light",    "#dcdcdc", "#f0f0f0", "#c8c8c8", "#18608E", "#1a1a1a", "#555555", "#b0b0b0", "#b2b2b2"),
            new("black",    "#000000", "#0d0d0d", "#161616", "#298DFF", "#ffffff", "#cccccc", "#2a2a2a", "#242424"),
            new("blood",    "#240c0d", "#2c1012", "#321416", "#e8485a", "#fffde8", "#f8c99e", "#401d1d", "#54201f"),
            new("greed",    "#002115", "#002e1c", "#003824", "#3fbf6f", "#fffde8", "#e0d49a", "#0f4a30", "#00593a"),
            new("cyanotic", "#001a28", "#00263a", "#002e48", "#3aa0d8", "#fffde8", "#e0d49a", "#183450", "#0a5478"),
        ];

        private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    }
}
