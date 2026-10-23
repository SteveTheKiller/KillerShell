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
            TitleSearchSurface.SizeChanged += (_, _) => UpdateTitleSearchPlacement();
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
            double anchorRight = SearchPanelBtn.ActualWidth > 0
                ? SearchPanelBtn.TranslatePoint(new Point(SearchPanelBtn.ActualWidth, 0), this).X
                : windowWidth - 132;
            double available = Math.Max(160, (anchorRight - 24) / scale);
            bool helpOpen = PatternHelpOverlay.Visibility == Visibility.Visible;
            bool helpBeside = helpOpen && available >= 660;
            double formWidth = Math.Min(360, available - (helpBeside ? 256 : 0));
            TitleSearchForm.Width = formWidth;
            TitleSearchLayout.Width = formWidth + (helpBeside ? 256 : 0);
            Grid.SetColumn(PatternHelpHost, helpBeside ? 1 : 0);
            Grid.SetRow(PatternHelpHost, helpBeside ? 0 : 1);
            PatternHelpOverlay.Width = helpBeside ? 240 : formWidth - 24;
            PatternHelpHost.Margin = helpOpen ? new Thickness(8, 8, 8, 8) : new Thickness(0);
            TitleSearchViewport.MaxHeight = Math.Max(80, (windowHeight - 80) / scale);
            UpdateTitleSearchPlacement();
        }

        private void UpdateTitleSearchPlacement()
        {
            // Include the measured scrollbar and help drawer when aligning the outer edge.
            double width = TitleSearchSurface.ActualWidth > 0
                ? TitleSearchSurface.ActualWidth : TitleSearchLayout.Width + 2;
            TitleSearchPopup.HorizontalOffset = SearchPanelBtn.ActualWidth - width * Math.Max(0.5, _appScale);
            TitleSearchPopup.VerticalOffset = 4;
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
