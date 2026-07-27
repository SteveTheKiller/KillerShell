using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KillerShell.Models
{
    // A group of search terms combined with one boolean mode. A file is a result
    // when it satisfies EVERY group (groups are AND-ed together); within a group,
    // ALL (And) or ANY (Or) of its terms must hit.
    public class TermGroup : INotifyPropertyChanged
    {
        public enum GroupMode { Or, And }

        private GroupMode _mode = GroupMode.Or;
        public GroupMode Mode
        {
            get => _mode;
            set { _mode = value; Notify(); RefreshLocalized(); }
        }

        // Localized: resolved through the app's locale resources so the toggle chip
        // and its tooltip follow a live language switch.
        public string ModeLabel => Mode == GroupMode.And
            ? L("Str_Group_All", "ALL")
            : L("Str_Group_Any", "ANY");
        public string ModeTooltip => Mode == GroupMode.And
            ? L("Str_TT_GroupAll", "ALL: a file must match every term in this group")
            : L("Str_TT_GroupAny", "ANY: a file matches if any term in this group hits");

        /// <summary>Re-raises the localized computed properties after a language switch.</summary>
        public void RefreshLocalized()
        {
            Notify(nameof(ModeLabel));
            Notify(nameof(ModeTooltip));
        }

        private static string L(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        public ObservableCollection<SearchTerm> Terms { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
