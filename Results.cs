using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;

// Results-pane interactions: expand/collapse, sorting, the Ctrl+F quick filter, and
// piping results into a new tab. Partial of MainWindow.
namespace KillerShell
{
    public partial class MainWindow
    {
        // ═══════════════════════════════════════════════════════════
        //  RESULT CLICK - single click expands, double click reveals
        // ═══════════════════════════════════════════════════════════
        // Click a card and it both selects and expands: the ListBox has already done the
        // selection on mouse-down by the time this runs, so the two do not compete.
        //
        // This used to clear the selection here, which made multi-select impossible in list view
        // and left drag-out with nothing to drag. Ctrl and Shift now suppress the expand toggle
        // instead, so a modifier-click is pure selection - otherwise building a selection would
        // expand every card you touched on the way.
        internal void ResultHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            bool extending = (System.Windows.Input.Keyboard.Modifiers &
                              (System.Windows.Input.ModifierKeys.Control |
                               System.Windows.Input.ModifierKeys.Shift)) != 0;

            // Expanding shows a hit's matched content lines, which only a SEARCH produces. While
            // browsing there are none, so the card opened to reveal the folder you are already
            // standing in - a click that cost a row of height and told you nothing.
            if (!extending && !_active.IsBrowsing
                && sender is FrameworkElement el && el.DataContext is SearchResult r)
                r.IsExpanded = !r.IsExpanded;

            e.Handled = true;
        }

        // Double-click means what it means in a file manager: enter the folder, or open the file.
        // While showing search results it still reveals in Explorer instead, because "open the
        // folder this hit is buried in" is what you want from a result and Show in Explorer is
        // the command that does it. Both live on the context menu either way.
        internal void ResultHeader_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            e.Handled = true;

            if (sender is not FrameworkElement el) return;

            if (_active.IsBrowsing && el.DataContext is SearchResult r) { ActivateEntry(r); return; }

            if (el.Tag is string path && System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        internal void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            bool expand = _active.Results.Any(r => !r.IsExpanded);
            foreach (var r in _active.Results) r.IsExpanded = expand;
            SetExpandAllLabel(expand);
        }

        // One glyph, two states: E740 expand-all / E73F collapse-all (codepoints keep
        // the source ASCII). The localized wording lives in the tooltip.
        private void SetExpandAllLabel(bool showCollapse)
        {
            Pane.ExpandAllGlyph.Text     = ((char)(showCollapse ? 0xE73F : 0xE740)).ToString();
            Pane.ExpandAllButton.ToolTip = Loc(showCollapse ? "Str_Btn_CollapseAll" : "Str_Btn_ExpandAll");
        }

        // ═══════════════════════════════════════════════════════════
        //  RESULT SORTING (like the HTML report's clickable columns)
        // ═══════════════════════════════════════════════════════════
        // The sort key is picked from a flyout rather than a combo (FilePane.xaml). The tab's
        // SortIndex was always the source of truth and the combo only mirrored it, so the old
        // _syncingSort guard is gone with it: nothing programs a selection any more, and a
        // click on a flyout row is by definition the user.
        internal void SortMenu_Click(object sender, RoutedEventArgs e)
        {
            SyncSortMenu();                     // opened cold - reflect the tab before it shows
            var p = Pane.SortPopup;
            p.IsOpen = !p.IsOpen;
            if (p.IsOpen && p.Child is UIElement child) Anim.FadeIn(child);
        }

        internal void SortItem_Click(object sender, RoutedEventArgs e)
        {
            Pane.SortPopup.IsOpen = false;
            if (_active == null) return;

            // The index rides on CommandParameter, not Tag: SurfaceButton's Tag="on" trigger is
            // what marks the chosen row, so Tag is already spoken for.
            if (sender is not Button b || !int.TryParse(b.CommandParameter as string, out int idx)) return;
            if (idx == _active.SortIndex) return;

            _active.SortIndex = idx;
            ApplySort(_active);
            SyncSortMenu();
        }

        /// <summary>
        /// Light the active tab's sort key in the flyout, through the same Tag="on" accent
        /// convention the toolbar toggles use.
        /// </summary>
        private void SyncSortMenu()
        {
            int idx = _active?.SortIndex ?? 1;
            Pane.SortFoundItem.Tag    = idx == 0 ? "on" : null;
            Pane.SortNameItem.Tag     = idx == 1 ? "on" : null;
            Pane.SortFolderItem.Tag   = idx == 2 ? "on" : null;
            Pane.SortSizeItem.Tag     = idx == 3 ? "on" : null;
            Pane.SortModifiedItem.Tag = idx == 4 ? "on" : null;
        }

        internal void SortDir_Click(object sender, RoutedEventArgs e)
        {
            _active.SortAsc = !_active.SortAsc;
            ApplySort(_active);
        }

        // Sorts through the collection VIEW, so the underlying results (and the order
        // the engine found them in) are untouched.
        //
        // A sort is deliberately NOT maintained while a search is running. With a
        // SortDescription on the view every single Add turns into a binary search plus a
        // List.Insert, and the shift cost grows with the list, so a run that returns tens
        // of thousands of hits does on the order of a billion element moves and the window
        // dies. While IsSearching the view is left unsorted - results append in discovery
        // order - and the tab's sort is re-applied in one pass when the run ends
        // (Search_Click's finally block). Changing the sort mid-run therefore records the
        // choice and reorders on completion rather than immediately.
        //
        // Safe to call for a background tab: the shared direction glyph is only touched
        // when the tab owns the UI.
        private void ApplySort(SearchTab t)
        {
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(t.Results);
            view.SortDescriptions.Clear();

            // MDL2 chevron up / down, built from codepoints so the source stays pure ASCII.
            // The details-view column arrows show the same state, so they follow along.
            if (t == _active)
            {
                Pane.SortDirButton.Content = ((char)(t.SortAsc ? 0xE70E : 0xE70D)).ToString();
                UpdateColumnArrows();   // ResultsView.cs
            }

            if (t.IsSearching) return;   // deferred - re-applied when the search finishes

            // Folders first while browsing, whatever the chosen key is, the way every file
            // manager does it. IsDirectory descending puts true before false. Search results are
            // all files, so this is skipped there rather than being a no-op sort on every add.
            if (t.IsBrowsing && FoldersOnTop)   // ViewOptions.cs
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                    nameof(SearchResult.IsDirectory), System.ComponentModel.ListSortDirection.Descending));

            string? prop = t.SortIndex switch
            {
                1 => nameof(SearchResult.FileName),
                2 => nameof(SearchResult.Directory),
                3 => nameof(SearchResult.SizeBytes),
                4 => nameof(SearchResult.Modified),
                _ => null,   // 0 = as found (Seq = discovery order)
            };
            // "as found" reverses too: descending on the discovery sequence.
            if (prop == null && !t.SortAsc) prop = nameof(SearchResult.Seq);
            if (prop != null)
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(prop,
                    t.SortAsc ? System.ComponentModel.ListSortDirection.Ascending
                              : System.ComponentModel.ListSortDirection.Descending));
        }

        // ═══════════════════════════════════════════════════════════
        //  RESULTS QUICK-FILTER (Ctrl+F)
        // ═══════════════════════════════════════════════════════════
        // Slide the bar down out of the pane's top edge (VS Code find-widget style).
        private void ShowResultFilterBar()
        {
            // Restore the slid position (fraction of pane width, like KillerPDF's AnnotBarFrac).
            if (double.TryParse(Services.ThemeManager.GetSetting("FilterBarFrac"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double frac) &&
                Pane.ResultsPane.ActualWidth > 0)
            {
                double right = Math.Max(2, Math.Min(frac * Pane.ResultsPane.ActualWidth,
                                                    Pane.ResultsPane.ActualWidth - 80));
                Pane.ResultFilterBar.Margin = new Thickness(0, 0, right, 0);
            }
            Pane.ResultFilterBar.Visibility = Visibility.Visible;
            var tt = new System.Windows.Media.TranslateTransform();
            Pane.ResultFilterBar.RenderTransform = tt;
            var ease = new System.Windows.Media.Animation.QuadraticEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
            Pane.ResultFilterBar.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            Pane.ResultFilterBox.Focus();
            Pane.ResultFilterBox.SelectAll();
        }

        // Debounced like KillerPDF's search bar: re-filtering a huge result list on
        // every keystroke stutters, so wait for a 250ms pause in typing.
        private System.Windows.Threading.DispatcherTimer? _filterDebounce;

        internal void ResultFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_active == null) return;
            _active.FilterText = Pane.ResultFilterBox.Text;

            if (_filterDebounce is null)
            {
                _filterDebounce = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _filterDebounce.Tick += (_, _) => { _filterDebounce!.Stop(); ApplyFilter(_active); };
            }
            _filterDebounce.Stop();
            _filterDebounce.Start();
        }

        internal void ResultFilterClose_Click(object sender, RoutedEventArgs e)
        {
            Pane.ResultFilterBox.Text = string.Empty;   // TextChanged clears the view filter
            Pane.ResultFilterBar.Visibility = Visibility.Collapsed;
        }

        // Filters the collection VIEW by name or path - the underlying results are untouched.
        private void ApplyFilter(SearchTab t)
        {
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(t.Results);
            string q = t.FilterText.Trim();
            view.Filter = q.Length == 0
                ? null
                : o => o is SearchResult r &&
                       (r.FileName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.Directory.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // 6-dot grip: slide the bar along the pane's top edge (like KillerPDF's
        // annotation bars). Position persists as a fraction of the pane width.
        private bool   _filterBarDrag;
        private double _filterBarGrabX;
        private double _filterBarStartRight;

        internal void FilterGrip_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _filterBarDrag       = true;
            _filterBarGrabX      = e.GetPosition(Pane.ResultsPane).X;
            _filterBarStartRight = Pane.ResultFilterBar.Margin.Right;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        internal void FilterGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_filterBarDrag) return;
            double dx = e.GetPosition(Pane.ResultsPane).X - _filterBarGrabX;
            double maxRight = Math.Max(2, Pane.ResultsPane.ActualWidth - Pane.ResultFilterBar.ActualWidth - 2);
            double right = Math.Min(maxRight, Math.Max(2, _filterBarStartRight - dx));
            Pane.ResultFilterBar.Margin = new Thickness(0, 0, right, 0);
        }

        internal void FilterGrip_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_filterBarDrag) return;
            _filterBarDrag = false;
            ((UIElement)sender).ReleaseMouseCapture();
            if (Pane.ResultsPane.ActualWidth > 0)
                Services.ThemeManager.SetSetting("FilterBarFrac",
                    (Pane.ResultFilterBar.Margin.Right / Pane.ResultsPane.ActualWidth)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // ═══════════════════════════════════════════════════════════
        //  PIPE - search within a search's results, in a new tab
        // ═══════════════════════════════════════════════════════════
        internal void PipeButton_Click(object sender, RoutedEventArgs e) => PipeIntoNewTab(_active);

        internal void PipeTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is SearchTab t) PipeIntoNewTab(t);
        }

        private void PipeIntoNewTab(SearchTab src)
        {
            // Pipe exactly what the user SEES: the collection view, so an active
            // Ctrl+F filter narrows what flows into the next search.
            var files = System.Windows.Data.CollectionViewSource.GetDefaultView(src.Results)
                .Cast<object>().OfType<SearchResult>().Select(r => r.FilePath).ToList();
            if (files.Count == 0)
            {
                SetTabStatusKey(_active, "Str_Status_NoPipe");
                return;
            }

            CaptureTab(_active);
            var t = CreateTab();
            var firstTerm = src.Groups.SelectMany(g => g.Terms)
                .Select(x => x.Pattern.Trim()).FirstOrDefault(p => p.Length > 0);
            string query = string.IsNullOrEmpty(src.QueryLabel)
                ? (firstTerm ?? string.Empty)
                : src.QueryLabel;

            t.PipeFiles = files;
            t.RootPath  = src.RootPath;
            // "375 results from ~\code  |  name: steve" - the query that produced
            // them makes the breadcrumb self-explanatory. Args stored raw so a
            // language switch can re-render the breadcrumb.
            t.PipeArgs  = [files.Count.ToString("N0"),
                string.IsNullOrEmpty(src.Title) ? src.RootPath : src.Title, query];
            t.PipeLabel = string.Format(Loc("Str_Pipe_Scope"), t.PipeArgs);

            // Tab title keeps the lineage readable: "~\code > steve".
            t.Title = $"{src.Title} > {(string.IsNullOrEmpty(firstTerm) ? files.Count.ToString("N0") : firstTerm)}";
            ActivateTab(t);
        }

        // ═══════════════════════════════════════════════════════════
        //  ROW ACTIONS (context menu + the inline row buttons)
        // ═══════════════════════════════════════════════════════════
        internal void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            // FrameworkElement, not MenuItem: the inline row buttons share this handler.
            if (sender is FrameworkElement fe && fe.Tag is string path && System.IO.File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }

        private void OpenWith_Click(object sender, RoutedEventArgs e)
        {
            // The Windows "Open with" chooser. OpenAs_RunDLL takes the rest of the
            // command line as the path - no quotes, even with spaces.
            if (sender is MenuItem mi && mi.Tag is string path && System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}");
        }

        internal void ShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string path && System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }
}
