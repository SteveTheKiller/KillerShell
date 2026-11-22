using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace KillerShell.Models
{
    /// <summary>All matches found in a single file across all search terms.</summary>
    public class SearchResult : INotifyPropertyChanged
    {
        public string FilePath  { get; set; } = string.Empty;
        public string FileName  { get; set; } = string.Empty;
        public string Directory { get; set; } = string.Empty;

        /// <summary>What the listing draws for the name: "Nickname (name)" when the item has a
        /// nickname (Services/Nicknames.cs), otherwise just the name. Sorting, type-ahead and
        /// Copy name keep using FileName.</summary>
        public string NameLabel => Services.Nicknames.Label(FilePath, FileName);

        /// <summary>The list item's accessible name, which UI Automation takes from the item.</summary>
        public override string ToString() => NameLabel;

        /// <summary>Redraws the name after its nickname was set or removed.</summary>
        public void NotifyNickname()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameLabel)));

        // Stat'd once by the engine when the file becomes a result (not per scanned
        // file), so the results view can sort by size / date like the HTML report.
        public long     SizeBytes { get; set; }
        public System.DateTime Modified { get; set; }

        // Discovery order, so "as found" can be reversed like any other sort key.
        public int Seq { get; set; }

        /// <summary>
        /// True when this entry is a folder rather than a file. Always false for a search
        /// result - the engine only ever produces files - and set when browsing a folder
        /// (Browse.cs), which lists directories and files into this same collection so that
        /// searching inside the folder you are looking at can put its hits in place rather
        /// than in a separate list.
        /// </summary>
        public bool IsDirectory { get; set; }

        public string SizeLabel
        {
            get
            {
                long b = SizeBytes;
                if (b <= 0) return string.Empty;
                if (b < 1024) return b + " B";
                double kb = b / 1024.0;
                if (kb < 1024) return kb.ToString("0") + " KB";
                double mb = kb / 1024.0;
                if (mb < 1024) return mb.ToString("0.0") + " MB";
                return (mb / 1024.0).ToString("0.00") + " GB";
            }
        }

        // ── Drive rows (This PC, Browse.cs ListDrives) ─────────────
        // A drive's capacity and free space, read once when This PC is listed. Zero total means
        // the volume did not answer (an empty card reader, a dead share), and the tile then
        // shows the name alone with no bar.
        public long DriveTotalBytes { get; set; }
        public long DriveFreeBytes  { get; set; }

        public bool HasDriveSpace => DriveTotalBytes > 0;

        /// <summary>The used share of the drive, 0 to 1, for the tile's bar.</summary>
        public double DriveUsedFraction => DriveTotalBytes <= 0 ? 0
            : System.Math.Min(1, System.Math.Max(0, (DriveTotalBytes - DriveFreeBytes) / (double)DriveTotalBytes));

        /// <summary>Over 90% full, which is where Explorer turns the bar red.</summary>
        public bool DriveNearlyFull => HasDriveSpace && DriveUsedFraction >= 0.9;

        /// <summary>"212 GB free of 476 GB" in the current language.</summary>
        public string DriveFreeLabel => !HasDriveSpace ? string.Empty
            : string.Format(Shell.MainWindow.LocStatic("Str_Drive_FreeOf"),
                            DriveSize(DriveFreeBytes), DriveSize(DriveTotalBytes));

        /// <summary>Three significant digits, the way Explorer writes drive sizes: 476 GB,
        /// 31.9 GB, 1.81 TB.</summary>
        internal static string DriveSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
            double v = System.Math.Max(0, bytes);
            int u = 0;
            while (v >= 1000 && u < units.Length - 1) { v /= 1024; u++; }
            string format = u == 0 || v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##";
            return v.ToString(format, System.Globalization.CultureInfo.CurrentCulture) + " " + units[u];
        }

        // The culture's short date and short time ("9/14/2026 10:09 PM" under en-US), which is
        // what Explorer's date columns show. One format for every date the listing draws.
        internal static string DateLabel(System.DateTime d) =>
            d == default ? string.Empty : d.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

        public string ModifiedLabel => DateLabel(Modified);

        // Stat'd alongside Modified wherever the listing already holds the file's info, so the
        // Created column and its sort never go back to the disk per row.
        public System.DateTime Created { get; set; }

        public string CreatedLabel => DateLabel(Created);

        /// <summary>The shell's name for this kind of file ("PDF Document", "File folder"),
        /// cached per extension (Services/ShellTypeNames.cs).</summary>
        public string TypeName => Services.ShellTypeNames.For(FilePath, IsDirectory);

        // Explorer's date buckets, as the key the details view groups on (DateGroups.cs).
        public int ModifiedGroup => Services.DateGroups.KeyFor(Modified);
        public int CreatedGroup  => Services.DateGroups.KeyFor(Created);

        /// <summary>Directory with zero-width break opportunities after each backslash,
        /// so wrapped paths break cleanly at separators instead of mid-name.</summary>
        public string DirectoryWrapped => Directory.Replace("\\", "\\" + (char)0x200B);

        // Collapsed by default: the list shows just the filename row; clicking a
        // result (or Expand all) reveals the folder + content matches beneath it.
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public List<TermMatch> Matches { get; set; } = [];

        public int TotalMatchCount => Matches.Sum(m => m.Lines.Count > 0 ? m.Lines.Count : 1);

        /// <summary>Real shell icon for this file type (cached per extension; resolved lazily
        /// when the virtualized row is realized, so only visible rows pay for it).</summary>
        public System.Windows.Media.ImageSource? Icon => Services.IconCache.For(FilePath, 32, IsDirectory);

        /// <summary>
        /// Applied after an on-disk rename so the SAME object updates in place instead of being
        /// removed-and-replaced (BrowseWatcher.ApplyWatchChanges). Keeping the object identity is
        /// the point: it is what selection and the details-pane preview are keyed on, and a
        /// remove-then-add briefly let a recycled virtualized row show another item's already-
        /// decoded thumbnail after a rename (2026-08-03).
        /// </summary>
        public void ApplyRename(string newPath, string newName)
        {
            FilePath = newPath;
            FileName = newName;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilePath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeName)));
        }

        /// <summary>Only matches with line hits - filename-term matches carry no useful
        /// detail rows (the query summary in the header already says what was searched).</summary>
        public IEnumerable<TermMatch> ContentMatches => Matches.Where(m => m.Lines.Count > 0);

        /// <summary>"(n)" when there is more than one hit; empty otherwise.</summary>
        public string CountBadge => TotalMatchCount > 1 ? $"({TotalMatchCount:N0})" : string.Empty;
    }

    /// <summary>Matches for one SearchTerm within a file.</summary>
    public class TermMatch
    {
        public SearchTerm Term { get; set; } = null!;

        /// <summary>
        /// Populated for Content terms.
        /// Empty for FileName terms (the filename itself is the match).
        /// </summary>
        public List<LineMatch> Lines { get; set; } = [];
    }

    /// <summary>A single matched line - WPF-bindable (properties, not ValueTuple fields).</summary>
    public class LineMatch
    {
        public int    LineNumber { get; set; }
        public string LineText   { get; set; } = string.Empty;
    }
}
