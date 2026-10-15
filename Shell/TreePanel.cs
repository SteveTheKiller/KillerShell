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
        private LinearGradientBrush? _treeEdgeMask;

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

            // Layout and extent changes can move the viewport without changing the tree's size.
            FolderTree.SizeChanged += (_, _) => SyncTreeFade();
            FolderTree.Loaded      += (_, _) => SyncTreeFade();

            // ScrollChanged bubbles from the tree's inner ScrollViewer, including extent changes.
            FolderTree.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
                new System.Windows.Controls.ScrollChangedEventHandler((_, _) => SyncTreeFade()));

            // Both masks belong to the content viewport, outside either scrollbar's subtree.
            KillerShell.Controls.TreeSideFade.Attach(FolderTree);
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

            if (sv.Template.FindName("ViewportFadeHost", sv) is not System.Windows.Controls.Border viewport) return;
            _treeEdgeMask ??= ((LinearGradientBrush)TreeFadeHost.Resources["TreeEdgeMask"]).Clone();
            double h = viewport.ActualHeight;
            if (h <= 1) return;                       // not laid out yet; a later pass covers it

            // EdgeFadeOpacity is 0 on a FLAT theme: 98SE has no soft edges anywhere - a list ends
            // at its sunken bevel, it does not dissolve - so the ramp is scaled to nothing rather
            // than special-cased here (2026-08-08).
            double fade = Application.Current.TryFindResource("EdgeFadeOpacity") is double e ? e : 1.0;

            double top = Ramp(sv.VerticalOffset, TopFadePx, 18) * fade;
            double bot = Ramp(sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset, BotFadePx, 22) * fade;

            // The mask only while an edge actually fades. A masked viewport renders its rows
            // through an intermediate layer, and text drawn there loses ClearType and goes soft
            // gray, which on 98SE (no fades at all) was every row all the time.
            viewport.OpacityMask = top > 0 || bot > 0 ? _treeEdgeMask : null;

            // The mask is on the viewport, so a stop's ALPHA is how much content survives there:
            // 0 at the outer edge means that row has dissolved and the window shows through.
            // Fully opaque when there is nothing past the edge, so an unscrolled tree is untouched.
            _treeEdgeMask.GradientStops[0].Color = Alpha(1 - top);
            _treeEdgeMask.GradientStops[3].Color = Alpha(1 - bot);

            // The viewport excludes both bars and the outer tree padding at every size.
            _treeEdgeMask.GradientStops[1].Offset = Math.Min(0.45, TopFadePx / h);
            _treeEdgeMask.GradientStops[2].Offset = Math.Max(0.5, 1 - BotFadePx / h);
        }

        private static Color Alpha(double a) =>
            Color.FromArgb((byte)Math.Round(Math.Min(1, Math.Max(0, a)) * 255), 0, 0, 0);

        private static double Ramp(double distance, double height, double fallback)
        {
            double h = double.IsNaN(height) || height <= 0 ? fallback : height;
            return Math.Min(1, Math.Max(0, distance) / h);
        }

        // Keep the existing layout-event entry point; the viewport now supplies the exact bounds.
        private void SyncTreeFade() => SyncTreeEdgeFades();

        private static double Clamp(double w)
            => Math.Max(TreeWidthMin, Math.Min(TreeWidthMax, w));

        private void TreePanel_Click(object sender, RoutedEventArgs e) => ToggleTreePanel();

        internal void ToggleTreePanel()
        {
            _treeOpen = !_treeOpen;
            if (_treeOpen) LoadDriveRoots();
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
            SyncTreeWatchers();   // TreeWatcher.cs - a closed tree watches nothing

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
