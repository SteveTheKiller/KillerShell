using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KillerShell.Models
{
    public class FileNode : INotifyPropertyChanged
    {
        // ── Identity ─────────────────────────────────────────────
        public string Name     { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool   IsDirectory { get; set; }

        /// <summary>Sentinel placeholder so the expand arrow appears before children load.</summary>
        public bool IsDummy { get; set; }

        // ── Tree state ───────────────────────────────────────────
        public ObservableCollection<FileNode> Children { get; set; } = [];

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; Notify(); }
        }

        // ── Search state ─────────────────────────────────────────
        private bool _hasMatches;
        public bool HasMatches
        {
            get => _hasMatches;
            set { _hasMatches = value; Notify(); Notify(nameof(NodeForeground)); }
        }

        /// <summary>Foreground color string used by the DataTemplate.</summary>
        public string NodeForeground => HasMatches ? "#1ea54c"
                                      : IsDirectory ? "#e8e8e8"
                                      : "#555555";

        /// <summary>Prefix glyph for the tree label.</summary>
        public string Glyph => IsDirectory ? "[/]" : "[ ]";

        // ── INotifyPropertyChanged ───────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
