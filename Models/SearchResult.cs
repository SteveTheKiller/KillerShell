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

        public string ModifiedLabel =>
            Modified == default ? string.Empty : Modified.ToString("yyyy-MM-dd HH:mm");

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

    /// <summary>A single matched line — WPF-bindable (properties, not ValueTuple fields).</summary>
    public class LineMatch
    {
        public int    LineNumber { get; set; }
        public string LineText   { get; set; } = string.Empty;
    }
}
