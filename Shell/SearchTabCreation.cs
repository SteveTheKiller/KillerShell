using KillerShell.Models;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private SearchTab CreateSearchFromTab(SearchTab source, string root)
        {
            var search = CreateTab();
            search.RootPath = root;
            search.CurrentFolder = root;
            search.Title = _titleSearchScope != 0 ? Loc(SearchScopeKey(_titleSearchScope))
                : string.IsNullOrEmpty(root) ? Loc("Str_Lbl_Results") : FolderTitle(root);
            search.IncludePatterns = source.IncludePatterns;
            search.ExcludePatterns = source.ExcludePatterns;
            search.CaseSensitive = source.CaseSensitive;
            search.Groups.Clear();
            foreach (var group in source.Groups)
            {
                var copy = new TermGroup { Mode = group.Mode };
                foreach (var term in group.Terms)
                    copy.Terms.Add(new SearchTerm { Mode = term.Mode, Pattern = term.Pattern });
                search.Groups.Add(copy);
            }
            search.Filters.Clear();
            foreach (var filter in source.Filters)
                search.Filters.Add(new SearchFilter
                {
                    FieldIndex = filter.FieldIndex, ConditionIndex = filter.ConditionIndex,
                    Text = filter.Text, Date = filter.Date, SizeText = filter.SizeText, UnitIndex = filter.UnitIndex
                });
            ActivateTab(search);
            return search;
        }
    }
}
