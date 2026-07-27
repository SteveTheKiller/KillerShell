using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KillerShell.Models
{
    public class SearchTerm : INotifyPropertyChanged
    {
        public enum SearchMode { FileName, Content }

        private SearchMode _mode = SearchMode.FileName;
        public SearchMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                Notify();
                Notify(nameof(IsContent));
                RefreshLocalized();
            }
        }

        public bool IsContent => Mode == SearchMode.Content;
        public string ModeLabel => Mode == SearchMode.FileName ? "F" : "C";

        // Localized: resolved through the app's locale resources so the chips and
        // tooltips follow a live language switch (RefreshLocalized re-raises them).
        public string ModeName => Mode == SearchMode.FileName
            ? L("Str_Mode_Name",    "name")
            : L("Str_Mode_Content", "content");
        public string ModeTooltip => Mode == SearchMode.FileName
            ? L("Str_TT_ModeName",    "Filename / wildcard  (e.g. *.log)")
            : L("Str_TT_ModeContent", "Content search  (text inside files)");

        /// <summary>Re-raises the localized computed properties after a language switch.</summary>
        public void RefreshLocalized()
        {
            Notify(nameof(ModeLabel));
            Notify(nameof(ModeName));
            Notify(nameof(ModeTooltip));
        }

        private static string L(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        private string _pattern = string.Empty;
        public string Pattern
        {
            get => _pattern;
            set { _pattern = value; Notify(); }
        }

        // -1 = not yet searched, ≥0 = match count from last run
        private int _matchCount = -1;
        public int MatchCount
        {
            get => _matchCount;
            set
            {
                _matchCount = value;
                Notify();
                Notify(nameof(MatchBadge));
            }
        }

        public string MatchBadge => _matchCount < 0 ? string.Empty : $"({_matchCount})";

        public void ResetCount() => MatchCount = -1;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
