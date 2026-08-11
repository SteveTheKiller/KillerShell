using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KillerShell.Shell
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
            // SyncTreeFade rides this too, not just SizeChanged above. The horizontal scrollbar
            // appears when the tree's CONTENT gets wider - expand a deep folder and the widest
            // label grows - which changes the ScrollViewer's ExtentWidth but NOT the TreeView's
            // own size, so SizeChanged never fired and the lift was never recomputed. The bottom
            // fade then sat straight over the scrollbar and grayed it out (2026-08-08).
            // ScrollChanged carries extent changes as well as scroll-position ones, which is
            // exactly the event that was missing.
            FolderTree.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
                new System.Windows.Controls.ScrollChangedEventHandler((_, _) =>
                {
                    SyncTreeEdgeFades();
                    SyncTreeFade();
                }));

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
        private const double TopFadePx = 30;
        private const double BotFadePx = 34;

        private void SyncTreeEdgeFades()
        {
            var sv = FindDescendant<System.Windows.Controls.ScrollViewer>(FolderTree);
            if (sv == null || TreeFadeHost == null) return;

            double h = TreeFadeHost.ActualHeight;
            if (h <= 1) return;                       // not laid out yet; a later pass covers it

            // EdgeFadeOpacity is 0 on a FLAT theme: 98SE has no soft edges anywhere - a list ends
            // at its sunken bevel, it does not dissolve - so the ramp is scaled to nothing rather
            // than special-cased here (2026-08-08).
            double fade = Application.Current.TryFindResource("EdgeFadeOpacity") is double e ? e : 1.0;

            double top = Ramp(sv.VerticalOffset, TopFadePx, 18) * fade;
            double bot = Ramp(sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset, BotFadePx, 22) * fade;

            // The mask is on the TREE, so a stop's ALPHA is how much of the tree survives there:
            // 0 at the outer edge means that row has dissolved and the window shows through.
            // Fully opaque when there is nothing past the edge, so an unscrolled tree is untouched.
            TreeFadeTopOuter.Color = Alpha(1 - top);
            TreeFadeBotOuter.Color = Alpha(1 - bot);

            // Where each ramp finishes, as a fraction of the host. The bottom one is lifted by the
            // horizontal scrollbar (SyncTreeFade below) so it dissolves the last ROW, not the bar.
            // The scrollbar sits inside this host, so the ramp has to END above it and full
            // opacity has to be restored below - otherwise the fade dissolves the scrollbar too.
            double bar = ScrollBarHeight();
            double barFrac = Math.Min(0.4, bar / h);

            TreeFadeTopInner.Offset = Math.Min(0.45, TopFadePx / h);
            TreeFadeBotInner.Offset = Math.Max(0.5, 1 - (BotFadePx + bar) / h);
            TreeFadeBotOuter.Offset = Math.Max(TreeFadeBotInner.Offset + 0.001, 1 - barFrac);
            TreeFadeBotRestore.Offset = Math.Min(1, TreeFadeBotOuter.Offset + 0.001);
        }

        private static Color Alpha(double a) =>
            Color.FromArgb((byte)Math.Round(Math.Min(1, Math.Max(0, a)) * 255), 0, 0, 0);

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
        private double ScrollBarHeight()
        {
            var sv = FindDescendant<System.Windows.Controls.ScrollViewer>(FolderTree);
            if (sv == null || sv.ComputedHorizontalScrollBarVisibility != Visibility.Visible) return 0;
            var bar = FindHorizontalBar(sv);
            return bar?.ActualHeight ?? SystemParameters.HorizontalScrollBarHeight;
        }

        /// <summary>
        /// Kept as the name the layout events call. It used to shorten the host by the scrollbar
        /// height so a separate fade Border sat above the bar; the fade is an OpacityMask on the
        /// host now, so shortening it would just crop the tree. The measurement moved into
        /// ScrollBarHeight above and the mask ends its ramp there instead.
        /// </summary>
        private void SyncTreeFade() => SyncTreeEdgeFades();

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

            // RIGHT, not Left - and that is not a typo for a left-hand panel.
            //
            // The frozen edge is the one the contents stay glued to while the column narrows,
            // so it decides which way the panel appears to travel. Pinned LEFT, the tree stood
            // still at the window's left edge and overflowed to the right; the results pane is
            // declared after it and so paints over it, which made the collapse read as the
            // window sliding IN over the top of the sidebar. Pinned RIGHT, the tree is glued to
            // the column's inner edge, so it travels LEFT with that edge and slides OUT under
            // the window's own frame - the direction the panel is actually going, and the
            // direction the chevron points. The overflow now runs off the left of the window,
            // where it is clipped for free instead of landing on the rail and the pane.
            SlideColumn(TreeCol, TreePanel, _treeOpen,
                        _treeWidth, minOpen: TreeWidthMin, maxOpen: TreeWidthMax,
                        freezeAlign: HorizontalAlignment.Right, animate: animate);
        }
    }
}
