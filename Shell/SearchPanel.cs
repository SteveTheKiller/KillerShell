using System;
using System.Windows;
using System.Windows.Controls;

namespace KillerShell.Shell
{
    // Reuse the search controls in a titlebar flyout without changing pane widths.
    public partial class MainWindow
    {
        private bool _searchOpen;

        private void InitSearchPanel()
        {
            if (SearchPanel.Parent is Panel parent) parent.Children.Remove(SearchPanel);
            TitleSearchContent.Content = SearchPanel;
            SearchPanel.Width = 320;
            SearchPanel.Visibility = Visibility.Visible;
            ScaleHost.SizeChanged += (_, _) => UpdateTitleSearchSize();
            _searchOpen = false;
            ApplySearchPanel(false);
        }

        private void SearchPanel_Click(object sender, RoutedEventArgs e) => ToggleSearchPanel();

        internal void ToggleSearchPanel()
        {
            _searchOpen = !_searchOpen;
            ApplySearchPanel(false);
            if (_searchOpen) FocusFirstTerm();
        }

        private void ApplySearchPanel(bool animate)
        {
            SearchPanelBtn.Tag = _searchOpen ? "on" : null;
            SearchCol.Width = new GridLength(0);
            SearchCol.MinWidth = 0;
            UpdateTitleSearchSize();
            SearchScopeButton.Content = Loc(SearchScopeKey(_titleSearchScope));
            SearchScopeButton.ToolTip = string.Join(Environment.NewLine, ResolveTitleSearchRoots());
            TitleSearchPopup.IsOpen = _searchOpen;
        }

        private void UpdateTitleSearchSize()
        {
            double windowHeight = ActualHeight > 0 ? ActualHeight : Height;
            double available = (Math.Min(windowHeight, SystemParameters.WorkArea.Height) - 100) / _appScale;
            TitleSearchViewport.MaxHeight = Math.Max(80, available - 52);
            // The form retains room for its existing minimum rows; the viewport scrolls it
            // when accessibility zoom or a short window leaves less space.
            SearchPanel.Height = Math.Max(320, Math.Min(560, TitleSearchViewport.MaxHeight));
        }

        internal void FocusSearchTerms()
        {
            if (!_searchOpen) { ToggleSearchPanel(); return; }
            FocusFirstTerm();
        }

        private void FocusFirstTerm()
        {
            Dispatcher.InvokeAsync(() =>
            {
                TermsList.UpdateLayout();
                var box = FindDescendant<TextBox>(TermsList);
                box?.Focus();
                box?.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}
