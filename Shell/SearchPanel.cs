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
            if (PatternHelpOverlay.Parent is Panel helpParent) helpParent.Children.Remove(PatternHelpOverlay);
            PatternHelpHost.Content = PatternHelpOverlay;
            SearchPanel.Visibility = Visibility.Visible;
            ScaleHost.SizeChanged += (_, _) => UpdateTitleSearchSize();
            LocationChanged += (_, _) => { if (_searchOpen) TitleSearchPopup.IsOpen = false; };
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
            SearchPanelBtn.Content = _searchOpen ? "\uE8BB" : "\uE721";
            SearchCol.Width = new GridLength(0);
            SearchCol.MinWidth = 0;
            UpdateTitleSearchSize();
            RefreshTitleSearchContext();
            TitleSearchPopup.IsOpen = _searchOpen;
        }

        private void RefreshTitleSearchContext()
        {
            bool withinResults = Pane.Active?.PipeFiles != null;
            SearchScopeLabel.SetResourceReference(TextBlock.TextProperty, withinResults
                ? "Str_Menu_PipeTab" : SearchScopeKey(_titleSearchScope));
            SearchScopeIcon.Text = withinResults ? "\uE71C" : _titleSearchScope == 2 ? "\uE7F4" : _titleSearchScope == 1 ? "\uE8F1" : "\uE737";
            SearchScopeButton.ToolTip = withinResults
                ? Pane.Active?.PipeLabel : string.Join(Environment.NewLine, ResolveTitleSearchRoots());
        }

        private void UpdateTitleSearchSize()
        {
            double windowHeight = ActualHeight > 0 ? ActualHeight : Height;
            double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            double scale = Math.Max(0.5, _appScale);
            TitleSearchToggle.Width = SearchPanelBtn.ActualWidth > 0 ? SearchPanelBtn.ActualWidth / scale : SearchPanelBtn.Width / scale;
            TitleSearchToggle.Height = SearchPanelBtn.ActualHeight > 0 ? SearchPanelBtn.ActualHeight / scale : SearchPanelBtn.Height / scale;
            TitleSearchToggle.FontSize = SearchPanelBtn.FontSize / scale;
            double available = Math.Max(220, (windowWidth - 48) / scale);
            bool helpOpen = PatternHelpOverlay.Visibility == Visibility.Visible;
            bool helpBeside = helpOpen && available >= 780;
            double formWidth = Math.Min(620, available - (helpBeside ? 256 : 0));
            TitleSearchForm.Width = formWidth;
            bool stacked = formWidth < 540;
            SearchTermsColumn.Width = new GridLength(stacked ? 1 : 1.1, GridUnitType.Star);
            SearchFiltersColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(SearchFiltersSection, stacked ? 0 : 1);
            Grid.SetRow(SearchFiltersSection, stacked ? 1 : 0);
            TitleSearchLayout.Width = formWidth + (helpBeside ? 256 : 0);
            Grid.SetColumn(PatternHelpHost, helpBeside ? 1 : 0);
            Grid.SetRow(PatternHelpHost, helpBeside ? 0 : 1);
            PatternHelpOverlay.Width = helpBeside ? 240 : formWidth - 24;
            PatternHelpHost.Margin = helpOpen ? new Thickness(8, 8, 8, 8) : new Thickness(0);
            TitleSearchViewport.MaxHeight = Math.Max(80, (windowHeight - 80) / scale);
            // Popup positioning already accounts for the outer shadow margin. Align the inner border.
            TitleSearchPopup.HorizontalOffset = SearchPanelBtn.ActualWidth - (formWidth + 1) * scale;
            TitleSearchPopup.VerticalOffset = -scale;
        }

        private void TitleSearchPopup_Opened(object sender, EventArgs e)
        {
            TitleSearchViewport.ScrollToTop();
            Anim.FadeIn(TitleSearchSurface);
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
