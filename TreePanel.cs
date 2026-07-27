using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace KillerShell
{
    // The folder tree panel's open/closed state and width. Partial of MainWindow.
    //
    // Open by default, unlike the search panel: KillerShell is a shell now, and a file manager
    // that starts with no way to get anywhere is just a list. Both the open/shut choice and the
    // dragged width are remembered.
    public partial class MainWindow
    {
        private bool _treeOpen = true;

        // Where the panel opens to. Seeded from the last drag, so a resize survives collapsing
        // the sidebar and restarting the app.
        private double _treeWidth = TreeWidthDefault;

        private const double TreeWidthDefault = 240;
        private const double TreeWidthMin     = 160;
        private const double TreeWidthMax     = 420;

        private void InitTreePanel()
        {
            // Defaults to CLOSED, so only an explicit "1" opens it. A first run should be the
            // listing and nothing else: the tiles are what the app is for, and a sidebar the
            // user did not ask for costs a column of them before they have seen the thing work.
            // The rail chevron is right there the moment they want it.
            _treeOpen = Services.ThemeManager.GetSetting("TreePanelOpen") == "1";

            // Invariant culture on the round trip: a saved "240.5" must not become unparseable
            // for anyone whose decimal separator is a comma.
            string saved = Services.ThemeManager.GetSetting("TreePanelWidth") ?? string.Empty;
            if (double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
                _treeWidth = Clamp(w);

            ApplyTreePanel(animate: false);   // startup should not slide

            // The fade has to track the horizontal scrollbar, and the scrollbar appears and
            // disappears as folders expand and the widest label changes - so this is driven off
            // layout rather than set once.
            FolderTree.SizeChanged += (_, _) => SyncTreeFade();
            FolderTree.Loaded      += (_, _) => SyncTreeFade();

            // Both edge fades follow the scroll position. ScrollChanged is handled at the
            // TreeView rather than dug out of its template: it bubbles, so the inner ScrollViewer
            // is reached without needing to have found it first. Loaded and SizeChanged are
            // covered too, for the passes where nothing scrolled but the extent moved.
            FolderTree.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
                new System.Windows.Controls.ScrollChangedEventHandler((_, _) => SyncTreeEdgeFades()));

            FolderTree.SizeChanged += (_, _) => SyncTreeEdgeFades();
            FolderTree.Loaded      += (_, _) => SyncTreeEdgeFades();
        }

        /// <summary>
        /// Fade each edge only while there is something PAST it, ramped over the fade's own
        /// height: none at the very top, none at the very bottom, full in between.
        /// </summary>
        /// <remarks>
        /// A proportional ramp rather than a flip at the ends. The fade exists to dissolve a row
        /// that is half gone, so at one pixel of scroll it should be one pixel's worth of fade;
        /// a hard on/off would pop the moment the wheel moved.
        ///
        /// The bottom one used to be pinned on, on the theory that a lazy load changing the
        /// content height would make it blink. It does not: the remaining distance is computed
        /// from the extent, and ScrollChanged fires when the extent changes as well as when the
        /// offset does, so an expanding folder just moves the ramp. Pinned on, it was drawing a
        /// fade over the last row of a tree that had nothing below it.
        /// </remarks>
        private void SyncTreeEdgeFades()
        {
            var sv = FindDescendant<System.Windows.Controls.ScrollViewer>(FolderTree);
            if (sv == null) return;

            TreeFadeTop.Opacity    = Ramp(sv.VerticalOffset, TreeFadeTop.Height, 18);
            TreeFadeBottom.Opacity = Ramp(sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset,
                                          TreeFadeBottom.Height, 22);
        }

        // Height is NaN until the border has been laid out, hence the fallback.
        private static double Ramp(double distance, double height, double fallback)
        {
            double h = double.IsNaN(height) || height <= 0 ? fallback : height;
            return Math.Min(1, Math.Max(0, distance) / h);
        }

        /// <summary>
        /// Keep the bottom edge fade sitting on the tree's last visible ROW rather than on the
        /// horizontal scrollbar underneath it.
        /// </summary>
        /// <remarks>
        /// The scrollbar's real height is measured rather than taken from SystemParameters: the
        /// tree uses the app's own themed scrollbar template, which is not the system metric, and
        /// on a scaled window (AppScale.cs) it is not that metric times anything predictable
        /// either. Measuring is the only version that stays right.
        /// </remarks>
        private void SyncTreeFade()
        {
            var sv = FindDescendant<System.Windows.Controls.ScrollViewer>(FolderTree);
            double lift = 0;

            if (sv != null && sv.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
            {
                var bar = FindHorizontalBar(sv);
                lift = bar?.ActualHeight ?? SystemParameters.HorizontalScrollBarHeight;
            }

            var m = TreeFadeBottom.Margin;
            if (Math.Abs(m.Bottom - lift) < 0.5) return;     // no churn on every layout pass
            TreeFadeBottom.Margin = new Thickness(m.Left, m.Top, m.Right, lift);
        }

        // FindDescendant takes the FIRST match of a type, and a ScrollViewer has two scrollbars,
        // so the orientation has to be checked rather than assumed.
        private static System.Windows.Controls.Primitives.ScrollBar? FindHorizontalBar(DependencyObject root)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is System.Windows.Controls.Primitives.ScrollBar b
                    && b.Orientation == System.Windows.Controls.Orientation.Horizontal) return b;

                var deeper = FindHorizontalBar(c);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private static double Clamp(double w)
            => Math.Max(TreeWidthMin, Math.Min(TreeWidthMax, w));

        private void TreePanel_Click(object sender, RoutedEventArgs e) => ToggleTreePanel();

        internal void ToggleTreePanel()
        {
            _treeOpen = !_treeOpen;
            ApplyTreePanel(animate: true);
            Services.ThemeManager.SetSetting("TreePanelOpen", _treeOpen ? "1" : "0");

            // Reopening should land on wherever the active tab already is rather than on
            // whatever was selected when it was closed.
            if (_treeOpen && _active.IsBrowsing && !string.IsNullOrEmpty(_active.CurrentFolder))
                _ = RevealInTree(_active.CurrentFolder!);
        }

        // ── Resize ───────────────────────────────────────────────
        // Driven off ActualWidth rather than an accumulated total, so the panel cannot drift
        // away from the pointer when a drag runs into the clamp and the mouse keeps going.
        private void TreeResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_treeOpen) return;

            double next = Clamp(TreeCol.ActualWidth + e.HorizontalChange);
            if (Math.Abs(next - _treeWidth) < 0.5) return;

            _treeWidth = next;

            // Straight to the column, no animation: a tween would lag the pointer. MinWidth has
            // to move with it, or a Grid column pinned at a MinWidth ignores the new Width.
            TreeCol.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, null);
            TreeCol.MinWidth = TreeWidthMin;
            TreeCol.MaxWidth = TreeWidthMax;
            TreeCol.Width    = new GridLength(_treeWidth);
        }

        private void TreeResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
            => Services.ThemeManager.SetSetting(
                   "TreePanelWidth", _treeWidth.ToString("0.##", CultureInfo.InvariantCulture));

        // The slide itself is shared with the search panel (PanelSlide.cs) so the two edges of
        // the window move identically.
        private void ApplyTreePanel(bool animate)
        {
            // Chevron points where the sidebar is going: left to tuck it away, right to bring
            // it back. Codepoints keep the source ASCII, as everywhere else in this project.
            SidebarToggleBtn.Content = ((char)(_treeOpen ? 0xE76B : 0xE76C)).ToString();

            // Nothing to grab while the panel is a zero-width column, and a live grip there
            // would sit on top of the rail.
            TreeResizeGrip.Visibility = _treeOpen ? Visibility.Visible : Visibility.Collapsed;

            TreeGapCol.Width = new GridLength(_treeOpen ? 6 : 0);

            // Left-hand panel, so its contents stay pinned to the LEFT edge during the tween.
            SlideColumn(TreeCol, TreePanel, _treeOpen,
                        _treeWidth, minOpen: TreeWidthMin, maxOpen: TreeWidthMax,
                        freezeAlign: HorizontalAlignment.Left, animate: animate);
        }
    }
}
