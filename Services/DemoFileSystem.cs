using System;
using System.Collections.Generic;

// The machine --demo pretends to be: one MSP field technician's workstation, invented whole.
//
// It exists so the surfaces that show a filesystem agree with each other. The folder tree
// (FolderTree.cs), the browse listings and This PC (Browse.cs) and the icon view all read this
// one table in demo mode, and the fabricated search results (DemoMode.cs) name paths out of it,
// so a capture with two of them on screen describes a single coherent machine. Three separately
// invented listings would contradict each other the moment they were photographed together,
// which is exactly what a marketing screenshot does.
//
// Everything here is FIXED - fixed names, fixed sizes, fixed dates, no DateTime.Now anywhere.
// That is the same intent as the fixed RNG seed in DemoMode.cs: a capture retaken next month
// has to match the one taken today, or the whole set has to be shot again.
//
// The content leans on what this app is for. Scripts, dated logs, exports, runbooks and ticket
// attachments are what a field tech's disk actually holds, and they are also what makes the
// .ps1 / .log / .reg highlighting and the "content: TODO" search look like themselves rather
// than like a demo.
//
// Every path here is syntactically legal Windows, which matters more than it looks:
// EnumerateChildren and ListFolder catch UnauthorizedAccessException and IOException but not
// ArgumentException, and the tree's expand handler is async void, so one illegal character
// would be an unhandled crash rather than an empty folder.
//
// The profile is "Demo" everywhere, and that is not cosmetic: a fabricated tree carrying a real
// account name would put a real person's directory layout into a public repository and into every
// capture taken from it. The name is fixed rather than read from the machine for the same reason
// the sizes and dates are - a capture has to be reproducible on any computer, so nothing here may
// depend on the one it was shot on.
namespace KillerShell.Services
{
    internal static class DemoFs
    {
        internal sealed class Entry
        {
            public string Name = string.Empty;
            public bool IsDir;
            public long Size;
            public DateTime Modified;

            // Derived rather than declared: 255 table rows each carrying a second hand-typed
            // date would drift and add nothing a capture can check. Hashed from the name, so it
            // is the same on every launch (the fixed-walk rule ImagePaths documents), and always
            // EARLIER than Modified, because a created-after-modified pair is the kind of
            // impossible detail a screenshot reader does notice. The details pane is the
            // consumer: it used to stat the fabricated path, get nothing, and show "-".
            public DateTime Created
            {
                get
                {
                    int h = 0;
                    foreach (char c in Name) h = h * 31 + c;
                    h &= 0x7FFFFFF;
                    return Modified.AddDays(-(1 + h % 400)).AddMinutes(-(h % 720));
                }
            }
        }

        private const long Kb = 1024L;
        private const long Mb = 1024L * 1024L;
        private const long Gb = 1024L * 1024L * 1024L;

        private static readonly Dictionary<string, List<Entry>> Table =
            new(StringComparer.OrdinalIgnoreCase);

        // The order Add() was called in. A Dictionary's enumeration order is an implementation
        // detail and is NOT promised to be insertion order, so anything that has to walk the whole
        // fabricated machine in a repeatable sequence walks this instead. ImagePaths below is the
        // reason it exists: it has to come out the same on every launch, or the set of paths that
        // draw a picture would shift between two captures of the same folder.
        private static readonly List<string> Order = [];

        private static readonly List<Entry> Nothing = [];

        private static readonly List<string> Roots = [@"C:\", @"D:\"];

        // What Services\DemoImages.cs treats as a picture. Deliberately the same set
        // Services\IconCache.cs calls decodable, so a row that gets an image icon in the listing
        // is exactly a row that has a picture drawn for it and nothing is left drawing a broken
        // half-state.
        private static readonly string[] ImageExtensions =
            [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp"];

        private static readonly object ImageGate = new();
        private static List<string>? _imagePaths;

        /// <summary>
        /// Every fabricated picture on the machine, full path, in one fixed order: folders in the
        /// order the table below declares them, files alphabetically inside each (which is the
        /// order Add already sorted them into). Services\DemoImages.cs uses it as the set of paths
        /// it will draw a picture for; the picture itself is derived from the path.
        /// </summary>
        /// <remarks>
        /// Built on first ASK rather than in a field initializer. Static field initializers run
        /// BEFORE the static constructor body, so a field-initialized version of this would have
        /// walked an empty Table and cached an empty list forever. Reading the property is a
        /// static member access, which forces the type initializer to have finished first.
        /// Locked because the listing decodes icons on background threads.
        /// </remarks>
        internal static IReadOnlyList<string> ImagePaths
        {
            get
            {
                lock (ImageGate)
                {
                    if (_imagePaths != null) return _imagePaths;

                    var found = new List<string>();
                    foreach (string folder in Order)
                        foreach (var e in Table[folder])
                        {
                            if (e.IsDir) continue;
                            foreach (string ext in ImageExtensions)
                                if (e.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                                {
                                    found.Add(System.IO.Path.Combine(folder, e.Name));
                                    break;
                                }
                        }

                    _imagePaths = found;
                    return _imagePaths;
                }
            }
        }

        // The fabricated machine's own special folders, mapped to the brand icon pack's art names
        // (Resources\icons, see Services\IconCache.cs).
        //
        // The real map IconCache builds asks Environment.GetFolderPath, which answers about the
        // machine the capture is being taken ON. That is exactly what demo mode must not consult:
        // on a machine whose profile is not named the same as the fabricated one, every fabricated
        // special folder would miss and draw the plain folder glyph, so the saved-places drawer -
        // the one surface whose whole job is to show the icon set off - would come out as a column
        // of identical yellow folders.
        private static readonly Dictionary<string, string> Art =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Users\Demo"]           = "home_folder_icon",
                [@"C:\Users\Demo\Desktop"]   = "desktop_folder_icon",
                [@"C:\Users\Demo\Documents"] = "documents_folder_icon",
                [@"C:\Users\Demo\Downloads"] = "downloads_folder_icon",
                [@"C:\Users\Demo\Pictures"]  = "pictures_folder_icon",
                [@"C:\Users\Demo\Music"]     = "music_folder_icon",
                [@"C:\Users\Demo\Videos"]    = "videos_folder_icon",
                [@"C:\Users\Demo\Favorites"] = "favorites_folder_icon",

                // Windows' own two odd ones. "Searches" is the Saved Searches folder and "Recent"
                // is the legacy junction onto Recent Items - both are real shell folders, which is
                // what earns the search-results and recents art a place in the drawer rather than
                // inventing a location to hang them on.
                [@"C:\Users\Demo\Searches"]  = "search_results_icon",
                [@"C:\Users\Demo\Recent"]    = "recents_icon",

                [@"C:\Program Files"]         = "program_files_icon",
                [@"C:\Program Files (x86)"]   = "program_files_icon",
                [@"C:\Windows"]               = "windows_folder_icon",
            };

        /// <summary>
        /// The brand art name for a fabricated special folder, or null for an ordinary one (the
        /// caller then draws the generic folder). Drive roots and This PC are answered by IconCache
        /// before this is reached, so they are deliberately absent.
        /// </summary>
        internal static string? ArtFor(string path)
            => string.IsNullOrEmpty(path) ? null
             : Art.TryGetValue(Key(path), out var name) ? name : null;

        /// <summary>The fabricated volumes, in the order the tree and This PC list them.</summary>
        internal static IReadOnlyList<string> Drives => Roots;

        /// <summary>True when <paramref name="path"/> is a folder on the fabricated machine.</summary>
        internal static bool Has(string path) => Table.ContainsKey(Key(path));

        /// <summary>
        /// What is inside <paramref name="path"/>. Empty rather than null for a folder that was
        /// never invented, so a caller can list one it has not heard of without checking first -
        /// which is what an expander arrow opening onto nothing already means in the tree.
        /// </summary>
        internal static IReadOnlyList<Entry> Children(string path)
            => Table.TryGetValue(Key(path), out var kids) ? kids : Nothing;

        /// <summary>
        /// The fabricated entry behind a full path, or null when the table has no such row (a
        /// drive root, or a path that was never fabricated). The details pane uses this for the
        /// fields a real row would be stat'd for - the fabricated path has no file behind it, so
        /// a stat answers nothing and the pane showed "-" where a capture wants a value.
        /// </summary>
        internal static Entry? Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            int cut = path.LastIndexOf('\\');
            if (cut <= 0 || cut == path.Length - 1) return null;   // a root, or trailing slash

            string folder = path[..cut];
            string name = path[(cut + 1)..];
            if (folder.Length == 2 && folder[1] == ':') folder += "\\";   // "C:" is drive-relative

            if (!Table.TryGetValue(Key(folder), out var kids)) return null;
            foreach (var e in kids)
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>
        /// "Local Disk (C:)" style label, the shape FolderNode.DriveLabel gives a real volume, so
        /// a fabricated drive is named the same way in the tree and in the This PC listing.
        /// </summary>
        internal static string DriveLabel(string root)
        {
            string key = Key(root);
            if (string.Equals(key, @"C:\", StringComparison.OrdinalIgnoreCase)) return "Local Disk (C:)";
            if (string.Equals(key, @"D:\", StringComparison.OrdinalIgnoreCase)) return "Field Backup (D:)";
            return key.TrimEnd('\\');
        }

        // One spelling per folder, so C:\Users\Demo and C:\Users\Demo\ land on the same row.
        // A BARE DRIVE ROOT keeps its separator: "C:\" trimmed to "C:" is not the drive, it is
        // the drive-RELATIVE path, which Windows resolves against a current directory - the same
        // trap ParentOf documents in Browse.cs.
        private static string Key(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string p = path.Trim();
            while (p.Length > 3 && (p[^1] == '\\' || p[^1] == '/'))
                p = p[..^1];
            return p;
        }

        // The clock part of a timestamp is DERIVED from the name rather than written out. Giving
        // every entry the same hour and minute made the modified column read as a wall of
        // identical times, and spelling one out per file would have tripled the table; a hash of
        // the name varies them and is still the same on every run, which is the whole point.
        private static DateTime Stamp(string name, int y, int m, int d)
        {
            int h = 0;
            foreach (char c in name) h = h * 31 + c;
            h &= 0x7FFFFFF;
            return new DateTime(y, m, d, 7 + h % 12, h % 60, h % 60);
        }

        private static Entry D(string name, int y, int m, int d)
            => new() { Name = name, IsDir = true, Modified = Stamp(name, y, m, d) };

        private static Entry F(string name, long size, int y, int m, int d)
            => new() { Name = name, Size = size, Modified = Stamp(name, y, m, d) };

        // Declaration order in the table below does not matter: each folder is sorted by name as
        // it is added, which is the order NTFS hands entries back in. That keeps the "as found"
        // sort looking like a real listing rather than like the order somebody typed the table.
        private static void Add(string folder, params Entry[] entries)
        {
            var list = new List<Entry>(entries);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // Recorded once per folder, on the first Add for it - a second Add for the same key
            // replaces the listing rather than adding the folder to the walk twice.
            string key = Key(folder);
            if (!Table.ContainsKey(key)) Order.Add(key);
            Table[key] = list;
        }

        static DemoFs()
        {
            // ── C: ───────────────────────────────────────────────
            Add(@"C:\",
                D("Program Files",       2026, 6, 18),
                D("Program Files (x86)", 2026, 4, 2),
                D("Tools",               2026, 6, 9),
                D("Users",               2026, 1, 14),
                D("Windows",             2026, 7, 2),
                F("pagefile.sys", 8 * Gb, 2026, 7, 3));

            Add(@"C:\Program Files",
                D("7-Zip",                2025, 11, 4),
                D("Common Files",         2026, 2, 19),
                D("Datto RMM",            2026, 5, 27),
                D("Git",                  2026, 3, 8),
                D("KillerShell",           2026, 7, 1),
                D("Microsoft Office",     2026, 1, 22),
                D("PowerShell",           2026, 6, 11),
                D("ScreenConnect Client", 2026, 6, 30),
                D("SentinelOne",          2026, 6, 24),
                D("Veeam",                2026, 5, 6),
                D("Windows Defender",     2026, 6, 28),
                D("WindowsApps",          2026, 4, 17));

            Add(@"C:\Program Files (x86)",
                D("Common Files",     2026, 2, 19),
                D("Internet Explorer", 2025, 9, 12),
                D("Microsoft",        2026, 1, 30),
                D("Mozilla Firefox",  2026, 6, 21),
                D("Notepad++",        2026, 3, 15),
                D("PDF24",            2026, 2, 3),
                D("TeamViewer",       2026, 5, 19),
                D("Windows Kits",     2025, 12, 8));

            Add(@"C:\Windows",
                D("Boot",      2025, 8, 14),
                D("Fonts",     2026, 6, 12),
                D("INF",       2026, 6, 29),
                D("Logs",      2026, 7, 2),
                D("Panther",   2026, 5, 3),
                D("Prefetch",  2026, 7, 3),
                D("System32",  2026, 7, 1),
                D("SysWOW64",  2026, 7, 1),
                D("Temp",      2026, 7, 3),
                D("WinSxS",    2026, 6, 28),
                D("servicing", 2026, 6, 28),
                F("explorer.exe",  5 * Mb,   2026, 6, 28),
                F("HelpPane.exe",  1090 * Kb, 2026, 3, 11),
                F("notepad.exe",   360 * Kb, 2026, 3, 11),
                F("system.ini",    219,      2025, 8, 14),
                F("win.ini",       92,       2025, 8, 14));

            // Portable utilities, the folder every field tech has. Also the one place in the
            // fabricated tree with .exe and .reg in it, which is what exercises the per-file
            // icon fallback in Services\IconCache.cs.
            Add(@"C:\Tools",
                D("Sysinternals", 2026, 4, 22),
                D("bin",          2026, 5, 15),
                F("autoruns.exe",        2100 * Kb, 2026, 4, 22),
                F("bginfo.exe",          1400 * Kb, 2026, 4, 22),
                F("disable-fastboot.reg", 412,      2026, 2, 9),
                F("Everything.exe",      2600 * Kb, 2026, 5, 15),
                F("nircmd.exe",           148 * Kb, 2025, 12, 1),
                F("procmon.exe",         3800 * Kb, 2026, 4, 22),
                F("PsExec.exe",           680 * Kb, 2026, 4, 22),
                F("rdp-tuning.reg",       268,      2026, 3, 27),
                F("rufus.exe",           1500 * Kb, 2026, 1, 18),
                F("WinDirStat.exe",       920 * Kb, 2025, 10, 6),
                F("wsus-client.reg",      704,      2026, 6, 2));

            // C:\Tools' two subfolders were declared but never filled, so expanding either of them
            // in the tree opened onto nothing. Both are the kind of folder the utilities above
            // actually come with.
            Add(@"C:\Tools\Sysinternals",
                F("accesschk.exe",  680 * Kb, 2026, 4, 22),
                F("Autologon.exe",  340 * Kb, 2026, 4, 22),
                F("Coreinfo.exe",   210 * Kb, 2026, 4, 22),
                F("Eula.txt",        7 * Kb,  2026, 4, 22),
                F("handle.exe",     420 * Kb, 2026, 4, 22),
                F("procexp64.exe", 2900 * Kb, 2026, 4, 22),
                F("RAMMap.exe",     980 * Kb, 2026, 4, 22),
                F("tcpvcon.exe",    260 * Kb, 2026, 4, 22),
                F("ZoomIt.exe",     620 * Kb, 2026, 4, 22));

            Add(@"C:\Tools\bin",
                F("curl.exe",     4600 * Kb, 2026, 5, 15),
                F("jq.exe",       1100 * Kb, 2026, 5, 15),
                F("rclone.exe",     58 * Mb, 2026, 5, 15),
                F("sqlite3.exe",  1300 * Kb, 2025, 12, 1),
                F("wget.exe",     4200 * Kb, 2026, 5, 15));

            // The app's own install folder, so a capture that browses to it shows something. The
            // exe name and the version folder agree with the fabricated process list and the fake
            // Application-log start-up event (Tools\ProcessListControl.cs, Tools\EventViewerControl.cs).
            Add(@"C:\Program Files\KillerShell",
                D("Modules", 2026, 7, 1),
                F("KillerShell.exe", 26 * Mb, 2026, 7, 1),
                F("LICENSE",         35 * Kb, 2026, 7, 1),
                F("README.md",       11 * Kb, 2026, 7, 1),
                F("unins000.exe",  1200 * Kb, 2026, 7, 1));

            Add(@"C:\Program Files\KillerShell\Modules",
                F("KillerScripts.psd1", 2 * Kb,  2026, 7, 1),
                F("KillerScripts.psm1", 28 * Kb, 2026, 7, 1));

            // ── The user ─────────────────────────────────────────
            // "Demo" is the only profile on the fabricated machine, and it is invented like
            // everything else here. It must never be a real account name: this table ships in a
            // public repository and every capture taken from it shows the full paths.
            Add(@"C:\Users",
                D("Demo",   2026, 7, 3),
                D("Public", 2025, 11, 2));

            Add(@"C:\Users\Public",
                D("Desktop",   2025, 11, 2),
                D("Documents", 2025, 11, 2),
                D("Downloads", 2025, 11, 2),
                D("Music",     2025, 11, 2),
                D("Pictures",  2025, 11, 2),
                D("Videos",    2025, 11, 2),
                F("desktop.ini", 174, 2025, 11, 2));

            // Every shell folder the brand icon pack has art for is present, and named exactly as
            // Windows names it - Favorites, Music, Recent, Searches and Videos are here for that
            // reason as much as for the listing. The saved-places drawer seeds itself from these
            // paths in demo mode (Shell\Bookmarks.cs SeedDemoBookmarks), so a folder missing here
            // would be a drawer row pointing at nothing.
            Add(@"C:\Users\Demo",
                D("AppData",   2026, 7, 3),
                D("code",      2026, 7, 2),
                D("Desktop",   2026, 7, 2),
                D("Documents", 2026, 6, 30),
                D("Downloads", 2026, 6, 27),
                D("Favorites", 2026, 5, 18),
                D("Logs",      2026, 7, 1),
                D("Music",     2026, 4, 26),
                D("Pictures",  2026, 6, 14),
                D("Recent",    2026, 7, 3),
                D("Searches",  2026, 6, 10),
                D("Videos",    2026, 2, 8),
                F("KillerShell.lnk", 2 * Kb,  2026, 7, 1),
                F("scratch.txt",    3 * Kb,  2026, 7, 3),
                F("todo.md",        6 * Kb,  2026, 7, 3));

            // Browser favorites, as Windows stores them: one .url per bookmark, in folders.
            Add(@"C:\Users\Demo\Favorites",
                D("Client portals", 2026, 5, 18),
                F("Datto RMM.url",           212, 2026, 2, 11),
                F("Microsoft 365 admin.url", 236, 2026, 1, 30),
                F("MXToolbox.url",           198, 2026, 4, 2),
                F("Windows Update catalog.url", 244, 2025, 12, 4));

            Add(@"C:\Users\Demo\Favorites\Client portals",
                F("Cedar Ridge - firewall.url", 224, 2026, 5, 18),
                F("Fairview - switch stack.url", 231, 2026, 5, 18),
                F("Lakeside - NAS.url",          217, 2026, 5, 18));

            // Saved Searches. The .search-ms files are what the search-results art on this folder
            // is actually about, so the folder holds them rather than being an empty shell.
            Add(@"C:\Users\Demo\Searches",
                F("Big files on D.search-ms",        3 * Kb, 2026, 6, 10),
                F("Invoices this year.search-ms",    3 * Kb, 2026, 6, 10),
                F("Logs changed today.search-ms",    2 * Kb, 2026, 6, 10),
                F("Scripts with TODO.search-ms",     3 * Kb, 2026, 6, 10));

            // Recent Items - shortcuts, which is exactly what the folder holds on a real profile.
            Add(@"C:\Users\Demo\Recent",
                F("Backup-Nightly.ps1.lnk",     1 * Kb, 2026, 7, 3),
                F("invoice_2026-06.pdf.lnk",    1 * Kb, 2026, 7, 2),
                F("killershell.log.lnk",        1 * Kb, 2026, 7, 3),
                F("Ransomware-First-Hour.md.lnk", 1 * Kb, 2026, 6, 16),
                F("Site Survey - Cedar Ridge.xlsx.lnk", 1 * Kb, 2026, 6, 5));

            // A field tech's music folder is the van playlist, and this is the only folder on the
            // fabricated machine with audio extensions in it - which is the point, since it is
            // what stops the icon view from drawing the same handful of glyphs everywhere.
            Add(@"C:\Users\Demo\Music",
                D("Van playlist", 2026, 4, 26),
                F("hold-music-loop.wav",  18 * Mb, 2026, 3, 9),
                F("voicemail-greeting.mp3", 640 * Kb, 2025, 11, 27));

            Add(@"C:\Users\Demo\Music\Van playlist",
                F("01 - drive time.mp3",   9 * Mb, 2026, 4, 26),
                F("02 - rack and roll.mp3", 8 * Mb, 2026, 4, 26),
                F("03 - patch panel.flac", 34 * Mb, 2026, 4, 26),
                F("playlist.m3u",          1 * Kb, 2026, 4, 26));

            // Screen recordings from site visits, plus the training clips a tech keeps around.
            Add(@"C:\Users\Demo\Videos",
                D("Screen recordings", 2026, 2, 8),
                F("cedarridge-cable-run.mp4",  340 * Mb, 2026, 6, 26),
                F("ups-alarm.mov",              72 * Mb, 2026, 2, 8));

            Add(@"C:\Users\Demo\Videos\Screen recordings",
                F("bitlocker-recovery-walkthrough.mp4", 210 * Mb, 2026, 1, 22),
                F("m365-handover.mkv",                  480 * Mb, 2026, 6, 25),
                F("printer-deploy.mp4",                  96 * Mb, 2026, 1, 29));

            Add(@"C:\Users\Demo\AppData",
                D("Local",    2026, 7, 3),
                D("LocalLow", 2026, 5, 20),
                D("Roaming",  2026, 7, 2));

            // Only the folders a tech would actually open, not the hundred a real profile has.
            // The point of carrying AppData at all is that walking down into it is a normal
            // troubleshooting move, and a tree that dead-ends after one level looks broken.
            Add(@"C:\Users\Demo\AppData\Local",
                D("KillerShell", 2026, 7, 3),
                D("Microsoft",  2026, 7, 3),
                D("Packages",   2026, 6, 21),
                D("Programs",   2026, 6, 5),
                D("Temp",       2026, 7, 3));

            Add(@"C:\Users\Demo\AppData\Local\KillerShell",
                D("native", 2026, 7, 1),
                F("crash.log",   4 * Kb, 2026, 6, 11),
                F("session.json", 9 * Kb, 2026, 7, 3));

            Add(@"C:\Users\Demo\AppData\Roaming",
                D("KillerTools", 2026, 7, 2),
                D("Microsoft",   2026, 7, 2),
                D("Notepad++",   2026, 3, 15));

            Add(@"C:\Users\Demo\AppData\Roaming\KillerTools",
                D("KillerShell", 2026, 7, 2),
                D("KillerNotes", 2026, 6, 28));

            Add(@"C:\Users\Demo\AppData\Roaming\KillerTools\KillerShell",
                F("bookmarks.txt", 1 * Kb, 2026, 7, 2),
                F("recents.txt",   2 * Kb, 2026, 7, 3),
                F("settings.ini",  6 * Kb, 2026, 7, 3));

            Add(@"C:\Users\Demo\Desktop",
                D("Ticket attachments", 2026, 7, 2),
                F("Cedar Ridge - core switch.txt", 14 * Kb,  2026, 6, 26),
                F("Fairview - RDP.lnk",             2 * Kb,  2026, 5, 30),
                F("handover.docx",                 48 * Kb,  2026, 6, 19),
                F("KillerShell.lnk",                 2 * Kb,  2026, 7, 1),
                F("onsite-checklist.pdf",         180 * Kb,  2026, 4, 11),
                F("screenshot-2026-07-02.png",   1450 * Kb,  2026, 7, 2),
                F("Ticket 48213 - notes.txt",       6 * Kb,  2026, 7, 2));

            Add(@"C:\Users\Demo\Desktop\Ticket attachments",
                F("48197-dxdiag.txt",      42 * Kb,  2026, 6, 17),
                F("48213-eventlog.evtx",  2100 * Kb, 2026, 7, 2),
                F("48213-ipconfig.txt",     4 * Kb,  2026, 7, 2),
                F("48244-msinfo32.nfo",   310 * Kb,  2026, 6, 29),
                F("48244-photo-rack.jpg", 3600 * Kb, 2026, 6, 29),
                F("48251-mxtoolbox.png",  260 * Kb,  2026, 7, 1));

            // The showcase folder for the icon view: four subfolders and a deliberately wide
            // spread of extensions, so every tile draws a different glyph.
            Add(@"C:\Users\Demo\Documents",
                D("Archive",  2026, 1, 6),
                D("Exports",  2026, 6, 30),
                D("Invoices", 2026, 6, 22),
                D("Runbooks", 2026, 6, 25),
                F("asset-photos.zip",               22 * Mb,  2026, 5, 23),
                F("backup-report.html",             88 * Kb,  2026, 7, 1),
                F("Client Onboarding.docx",         62 * Kb,  2026, 3, 4),
                F("contacts.vcf",                    4 * Kb,  2025, 12, 15),
                F("Fairview - network diagram.vsdx", 1150 * Kb, 2026, 2, 27),
                F("invoice_template.docx",          28 * Kb,  2025, 9, 3),
                F("logo-fairview.png",             210 * Kb,  2026, 1, 20),
                F("meeting-notes.one",             512 * Kb,  2026, 6, 18),
                F("msp-rate-card.xlsx",             41 * Kb,  2026, 4, 30),
                F("Password Policy.pdf",           240 * Kb,  2026, 2, 12),
                F("quarterly-review.pptx",        3200 * Kb,  2026, 6, 30),
                F("scope-of-work.txt",               9 * Kb,  2026, 5, 8),
                F("Site Survey - Cedar Ridge.xlsx", 96 * Kb,  2026, 6, 5),
                F("warranty-lookup.csv",            18 * Kb,  2026, 6, 11));

            // A year of them, the same twelve months the fabricated name search finds
            // (DemoMode.cs tab 1 walks 2025-07 forward). The two listings have to name the same
            // files, or a capture of the search beside a capture of the folder shows two
            // different machines.
            Add(@"C:\Users\Demo\Documents\Invoices",
                F("invoice_2025-07.pdf",  52 * Kb, 2025, 7, 14),
                F("invoice_2025-08.pdf",  61 * Kb, 2025, 8, 12),
                F("invoice_2025-09.pdf",  47 * Kb, 2025, 9, 15),
                F("invoice_2025-10.pdf", 118 * Kb, 2025, 10, 13),
                F("invoice_2025-11.pdf",  74 * Kb, 2025, 11, 12),
                F("invoice_2025-12.pdf", 156 * Kb, 2025, 12, 16),
                F("invoice_2026-01.pdf",  69 * Kb, 2026, 1, 14),
                F("invoice_2026-02.pdf",  93 * Kb, 2026, 2, 11),
                F("invoice_2026-03.pdf", 212 * Kb, 2026, 3, 13),
                F("invoice_2026-04.pdf",  88 * Kb, 2026, 4, 14),
                F("invoice_2026-05.pdf", 134 * Kb, 2026, 5, 12),
                F("invoice_2026-06.pdf", 101 * Kb, 2026, 6, 15),
                F("invoices.xlsx",       184 * Kb, 2026, 6, 22));

            Add(@"C:\Users\Demo\Documents\Archive",
                D("2025", 2026, 1, 6),
                F("contracts-2024.zip",     12 * Mb,  2025, 1, 8),
                F("legacy-rate-card.xlsx",  36 * Kb,  2024, 11, 19),
                F("old invoices.zip",     6100 * Kb,  2024, 12, 30),
                F("old-runbooks.7z",         4 * Mb,  2025, 3, 2),
                F("tax-2024.pdf",         1900 * Kb,  2025, 4, 9));

            Add(@"C:\Users\Demo\Documents\Archive\2025",
                F("invoices-2025.zip",     9 * Mb,  2026, 1, 6),
                F("site-photos-2025.zip", 480 * Mb, 2026, 1, 6),
                F("tickets-2025.csv",     820 * Kb, 2026, 1, 6),
                F("timesheets-2025.xlsx",  74 * Kb, 2026, 1, 6));

            Add(@"C:\Users\Demo\Documents\Runbooks",
                F("Backup-Verification.md",     14 * Kb, 2026, 5, 21),
                F("DR-Test-Checklist.md",        9 * Kb, 2026, 3, 18),
                F("Firewall-Failover.docx",     58 * Kb, 2026, 4, 7),
                F("index.md",                    3 * Kb, 2026, 6, 25),
                F("M365-Tenant-Handover.md",    22 * Kb, 2026, 6, 25),
                F("New-Client-Onboarding.docx", 84 * Kb, 2026, 2, 24),
                F("Offboarding-Checklist.docx", 46 * Kb, 2026, 6, 3),
                F("Printer-Deployment.md",      11 * Kb, 2026, 1, 29),
                F("Ransomware-First-Hour.md",   18 * Kb, 2026, 6, 16),
                F("Server-Patch-Window.md",     16 * Kb, 2026, 6, 20),
                F("VPN-Split-Tunnel.docx",      52 * Kb, 2026, 5, 13));

            Add(@"C:\Users\Demo\Documents\Exports",
                F("ad-users-2026-06-30.csv",  340 * Kb, 2026, 6, 30),
                F("bitlocker-keys.csv",        26 * Kb, 2026, 6, 12),
                F("defender-alerts.json",    1200 * Kb, 2026, 6, 29),
                F("killershell-report.html",   780 * Kb, 2026, 6, 30),
                F("licenses-fairview.xlsx",    64 * Kb, 2026, 5, 28),
                F("patch-compliance.csv",     128 * Kb, 2026, 6, 21),
                F("stale-profiles.csv",        44 * Kb, 2026, 6, 8),
                F("warranty-expiry.csv",       31 * Kb, 2026, 4, 25));

            Add(@"C:\Users\Demo\Downloads",
                D("old", 2026, 2, 14),
                F("agent-setup.exe",                  68 * Mb,  2026, 6, 24),
                F("driver-pack-latitude-7450.zip",   410 * Mb,  2026, 6, 17),
                F("firmware-ex4300-21.4R3.tgz",      640 * Mb,  2026, 5, 29),
                F("LAPS.msi",                       1400 * Kb,  2026, 3, 12),
                F("nmap-7.95-setup.exe",              32 * Mb,  2026, 4, 5),
                F("PowerShell-7.5.0-win-x64.msi",    104 * Mb,  2026, 6, 11),
                F("rufus-4.6.exe",                  1500 * Kb,  2026, 1, 18),
                F("veeam-agent.zip",                 780 * Mb,  2026, 6, 27),
                F("WindowsSensor.exe",                22 * Mb,  2026, 6, 27));

            Add(@"C:\Users\Demo\Downloads\old",
                F("chrome-enterprise.msi",  92 * Mb,  2025, 10, 3),
                F("dotnet48-offline.exe",  110 * Mb,  2025, 8, 22),
                F("teamviewer-host.msi",    38 * Mb,  2026, 2, 14));

            // Dated logs, an .err and a .trace: what the Log highlighting was written for, and
            // the folder the canned shell session lists (DemoMode.cs) - the sizes there are
            // these sizes, so the terminal and the listing cannot disagree.
            Add(@"C:\Users\Demo\Logs",
                D("archive", 2026, 6, 1),
                F("agent-2026-06-27.log",           274432, 2026, 6, 27),
                F("agent-2026-06-28.log",           298496, 2026, 6, 28),
                F("agent-2026-06-29.log",           311296, 2026, 6, 29),
                F("agent-install.trace",             96 * Kb, 2026, 6, 24),
                F("backup-nightly-2026-07-01.log",  184320, 2026, 7, 1),
                F("deploy-agent-2026-06-30.log",     52736, 2026, 6, 30),
                F("killershell.log",                  18 * Kb, 2026, 7, 3),
                F("patch-window.err",                14208, 2026, 6, 28),
                F("winrm-debug.log",                 42 * Kb, 2026, 6, 19));

            Add(@"C:\Users\Demo\Logs\archive",
                F("agent-2026-05.zip",    4 * Mb,  2026, 6, 1),
                F("agent-2026-06-01.log", 262 * Kb, 2026, 6, 1),
                F("backup-2026-05.zip",   9 * Mb,  2026, 6, 1),
                F("patch-2026-Q1.zip",    6 * Mb,  2026, 4, 2));

            // ── The picture folders ──────────────────────────────
            // These carry more entries than anywhere else on the fabricated machine on purpose:
            // they are the icon view's and the details strip's showcase. Every name here has a
            // picture drawn for it from the name itself (Services\DemoImages.cs), so a tile grid
            // or a preview taken here shows actual images rather than a wall of identical
            // file-type glyphs - which is the one thing a browser screenshot has to demonstrate
            // and the only thing a fabricated path cannot do by itself.
            Add(@"C:\Users\Demo\Pictures",
                D("Screenshots", 2026, 7, 2),
                D("Site photos", 2026, 6, 29),
                D("Wallpapers",  2026, 1, 3),
                F("bench-overview.jpg",       3100 * Kb, 2026, 5, 2),
                F("cable-labels.jpg",         1900 * Kb, 2026, 4, 18),
                F("headshot.jpg",             2100 * Kb, 2025, 9, 26),
                F("logo-killershell.png",      180 * Kb, 2026, 3, 30),
                F("parts-bin.jpg",            2400 * Kb, 2026, 2, 21),
                F("rack-fairview.jpg",        4200 * Kb, 2026, 6, 14),
                F("serial-plate-nas01.jpg",   1500 * Kb, 2026, 3, 5),
                F("van-kit-layout.jpg",       3300 * Kb, 2025, 10, 30),
                F("wallpaper.jpg",            3600 * Kb, 2026, 1, 3),
                F("whiteboard-vlan-plan.jpg", 2800 * Kb, 2026, 6, 3));

            Add(@"C:\Users\Demo\Pictures\Screenshots",
                F("ks-browse.png",   1800 * Kb, 2026, 7, 2),
                F("ks-details.png",  1700 * Kb, 2026, 7, 2),
                F("ks-icons.png",    2400 * Kb, 2026, 7, 2),
                F("ks-registry.png", 1300 * Kb, 2026, 7, 2),
                F("ks-search.png",   1600 * Kb, 2026, 7, 2),
                F("ks-terminal.png", 1200 * Kb, 2026, 7, 2),
                F("ks-tree.png",      940 * Kb, 2026, 7, 2));

            Add(@"C:\Users\Demo\Pictures\Site photos",
                F("cedarridge-comms-room.jpg",  3800 * Kb, 2026, 6, 26),
                F("cedarridge-patch-panel.jpg", 4100 * Kb, 2026, 6, 26),
                F("cedarridge-server-shelf.jpg", 3600 * Kb, 2026, 6, 26),
                F("fairview-cable-tray.jpg",    3200 * Kb, 2026, 6, 29),
                F("fairview-rack-front.jpg",    3400 * Kb, 2026, 6, 29),
                F("fairview-rack-rear.jpg",     3900 * Kb, 2026, 6, 29),
                F("fairview-ups.jpg",           2700 * Kb, 2026, 6, 29),
                F("lakeside-nas-bay.jpg",       3100 * Kb, 2026, 6, 12),
                F("lakeside-patch-room.jpg",    3500 * Kb, 2026, 6, 12),
                F("lakeside-wap-ceiling.jpg",   2200 * Kb, 2026, 6, 12));

            Add(@"C:\Users\Demo\Pictures\Wallpapers",
                F("bone-grain-3840.png",   4800 * Kb, 2026, 1, 3),
                F("carbon-dark-2560.png",  2900 * Kb, 2025, 12, 19),
                F("circuit-teal-3840.jpg", 3700 * Kb, 2026, 1, 3),
                F("grid-red-1920.png",     1600 * Kb, 2025, 11, 8),
                F("static-noise-2560.jpg", 2100 * Kb, 2026, 1, 3),
                F("terminal-green-3840.jpg", 3400 * Kb, 2025, 12, 19));

            // ── Code ─────────────────────────────────────────────
            Add(@"C:\Users\Demo\code",
                D("homelab",        2026, 1, 9),
                D("KillerShell",     2026, 7, 3),
                D("killer-scripts", 2026, 6, 2),
                F("README.md",                2 * Kb, 2026, 4, 16),
                F("workspace.code-workspace", 1 * Kb, 2026, 4, 16));

            // The three .ps1 files the fabricated content search already reports live here, with
            // the module and the tests a scripts repo of that size would really have.
            Add(@"C:\Users\Demo\code\killer-scripts",
                D("docs",  2026, 5, 9),
                D("tests", 2026, 6, 2),
                F(".gitignore",              512,     2025, 10, 21),
                F("Backup-Nightly.ps1",      12 * Kb, 2026, 5, 14),
                F("Deploy-Agent.ps1",         9 * Kb, 2026, 6, 2),
                F("Get-StaleProfiles.ps1",    6 * Kb, 2026, 3, 21),
                F("Invoke-PatchWindow.ps1",  14 * Kb, 2026, 5, 30),
                F("KillerOps.psd1",           3 * Kb, 2026, 6, 2),
                F("KillerOps.psm1",          34 * Kb, 2026, 6, 2),
                F("New-ClientTenant.ps1",    11 * Kb, 2026, 4, 28),
                F("README.md",                6 * Kb, 2026, 6, 2),
                F("Remove-OldProfiles.ps1",   5 * Kb, 2026, 2, 17),
                F("Sync-BitlockerKeys.ps1",   7 * Kb, 2026, 6, 12),
                F("Test-BackupRestore.ps1",   8 * Kb, 2026, 6, 2));

            Add(@"C:\Users\Demo\code\killer-scripts\tests",
                F("Backup-Nightly.Tests.ps1",    5 * Kb, 2026, 5, 14),
                F("Deploy-Agent.Tests.ps1",      4 * Kb, 2026, 6, 2),
                F("Get-StaleProfiles.Tests.ps1", 3 * Kb, 2026, 3, 21),
                F("KillerOps.Tests.ps1",         9 * Kb, 2026, 6, 2));

            Add(@"C:\Users\Demo\code\killer-scripts\docs",
                F("conventions.md",  4 * Kb, 2026, 5, 9),
                F("parameters.md",   7 * Kb, 2026, 5, 9),
                F("release-notes.md", 5 * Kb, 2026, 6, 2));

            Add(@"C:\Users\Demo\code\homelab",
                D("pihole",  2026, 1, 9),
                D("traefik", 2026, 1, 9),
                F(".env",               512,     2026, 1, 9),
                F("docker-compose.yml", 4 * Kb,  2026, 1, 9),
                F("notes.md",           7 * Kb,  2026, 1, 9),
                F("README.md",          3 * Kb,  2025, 12, 28),
                F("Rotate-Certs.ps1",   8 * Kb,  2026, 1, 9));

            Add(@"C:\Users\Demo\code\homelab\traefik",
                F("dynamic.yml",  3 * Kb, 2026, 1, 9),
                F("traefik.yml",  2 * Kb, 2026, 1, 9));

            Add(@"C:\Users\Demo\code\homelab\pihole",
                F("adlists.list",  9 * Kb, 2026, 1, 9),
                F("custom.list",   1 * Kb, 2026, 1, 9));

            Add(@"C:\Users\Demo\code\KillerShell",
                D("Models",   2026, 7, 3),
                D("Services", 2026, 7, 3),
                D("Strings",  2026, 6, 26),
                D("Terminal", 2026, 7, 2),
                F("CHANGELOG.md",      24 * Kb, 2026, 7, 3),
                F("KillerShell.csproj",  6 * Kb, 2026, 7, 1),
                F("KillerShell.sln",     2 * Kb, 2026, 4, 4),
                F("MainWindow.xaml",   84 * Kb, 2026, 7, 3),
                F("README.md",         11 * Kb, 2026, 7, 3));

            // ── D: the field drive ───────────────────────────────
            Add(@"D:\",
                D("Backups", 2026, 7, 1),
                D("Exports", 2026, 6, 30),
                D("Images",  2026, 5, 22),
                F("_index.txt", 3 * Kb, 2026, 7, 1));

            Add(@"D:\Backups",
                D("archive", 2026, 5, 31),
                F("backup-log-2026-07-01.log",           220 * Kb, 2026, 7, 1),
                F("cedarridge-fs01-2026-06-30.vbk",       28 * Gb, 2026, 6, 30),
                F("fairview-dc01-2026-06-28.vbk",         42 * Gb, 2026, 6, 28),
                F("fairview-dc01-2026-07-01.vib",          3 * Gb, 2026, 7, 1),
                F("demo-workstation-2026-06-25.vhdx",     96 * Gb, 2026, 6, 25));

            Add(@"D:\Backups\archive",
                F("cedarridge-fs01-2026-05-31.vbk", 27 * Gb, 2026, 5, 31),
                F("fairview-dc01-2026-05-31.vbk",   41 * Gb, 2026, 5, 31));

            Add(@"D:\Exports",
                F("ad-inventory-2026-06.csv",   410 * Kb, 2026, 6, 30),
                F("asset-register.csv",         162 * Kb, 2026, 6, 30),
                F("licenses-all-tenants.xlsx",  240 * Kb, 2026, 6, 30),
                F("m365-mailboxes.csv",          88 * Kb, 2026, 6, 30),
                F("tickets-q2-2026.csv",       1100 * Kb, 2026, 6, 30));

            Add(@"D:\Images",
                D("drivers", 2026, 5, 22),
                F("install-fairview.wim",                    12 * Gb,  2026, 5, 22),
                F("ubuntu-24.04.2-live-server-amd64.iso",     2 * Gb,  2026, 3, 19),
                F("Win10_22H2_x64.iso",                       5 * Gb,  2025, 11, 8),
                F("Win11_24H2_English_x64.iso",               6 * Gb,  2026, 4, 2),
                F("WinPE-KillerShell.iso",                   900 * Mb,  2026, 5, 22));

            Add(@"D:\Images\drivers",
                F("dell-latitude-7450.cab", 780 * Mb, 2026, 5, 22),
                F("hp-elitebook-860.cab",   640 * Mb, 2026, 5, 22),
                F("lenovo-t14s-gen5.cab",   590 * Mb, 2026, 5, 22));
        }
    }
}
