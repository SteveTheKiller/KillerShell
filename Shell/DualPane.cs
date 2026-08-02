using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;

// Dual pane: the second FilePane, the splitter, and which pane has focus. Partial of MainWindow.
//
// The state that makes this work landed earlier and lives elsewhere: a tab belongs to a PANE
// (FilePane.Tabs / FilePane.Active), and every command reaches its pane through Panes.cs, whose
// `Pane` property resolves to whichever one has focus. So nothing in Tabs.cs, Results.cs,
// FileCommands.cs or the rest needed touching to become dual-pane aware - they were already
// asking "the focused pane" rather than naming LeftPane.
//
// The folder tree and the search panel stay window-wide and retarget with focus, rather than
// being duplicated per pane: clicking in a pane makes it the target, and the tree and SEARCH
// button then act on it. That is the dual-pane convention, and two search panels side by side
// would cost more width than the window has. It does mean the focused pane must be visibly
// marked, or you lose track of where the tree is about to send you - hence the accent ring.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>Second pane showing. Persisted so the layout survives a restart.</summary>
        internal bool DualPane { get; private set; }

        /// <summary>True = side by side (columns), false = stacked (rows).</summary>
        private bool _paneSideBySide = true;

        /// <summary>What a pane keeps between itself and the window edge.</summary>
        private const double PaneEdge = 8;

        /// <summary>The splitter track, which is also the drag target for resizing the split.</summary>
        private const double SplitPx = 4;

        // The channel between two side-by-side panes is the splitter plus this, and the two are
        // sized so that it comes to exactly PaneEdge: the gap either side of a pane then reads
        // the same whether its neighbor is the other pane or the window edge.
        //
        // Derived rather than typed as a number, so changing the splitter width keeps the rule
        // true instead of quietly widening the channel.
        private const double PaneGutter = PaneEdge - SplitPx;

        // ═══════════════════════════════════════════════════════════
        //  OPEN / CLOSE
        // ═══════════════════════════════════════════════════════════
        internal void ToggleDualPane()
        {
            if (DualPane) CloseSecondPane();
            else          OpenSecondPane();
        }

        // Toolbar button (FilePane.xaml). Left-click opens/closes, right-click flips the
        // orientation - a second button for orientation would sit dead most of the time.
        internal void DualPane_Click(object sender, RoutedEventArgs e) => ToggleDualPane();

        internal void DualPaneOrient_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!DualPane) return;   // nothing to reorient with one pane
            TogglePaneOrientation();
            e.Handled = true;
        }

        private void OpenSecondPane()
        {
            if (DualPane) return;
            DualPane = true;

            // The second pane opens on the SAME folder as the one you were in. Opening it empty
            // or at home would make the first thing you do every time "navigate it back to where
            // I already was" - the point of a second pane is usually to work between two places,
            // and this one starts as a copy you can then move.
            var from = Pane.Active;
            string? folder = from != null && from.IsBrowsing ? from.CurrentFolder : null;
            RightPane.Visibility = Visibility.Visible;

            ApplyPaneLayout(animate: true);
            // Whether the band shows is a two-pane decision now (Tabs.cs), so opening the
            // second pane can turn the first one's band on - and has to, or the two pane tops
            // sit at different heights.
            UpdateTabBar();

            if (RightPane.Tabs.Count == 0)
            {
                RightPane.TabStrip.ItemsSource = RightPane.Tabs;
                SeedPane(RightPane, folder);   // async; sets the focus ring when it lands
            }
            else
            {
                UpdatePaneFocusRing();
            }
        }

        private void CloseSecondPane()
        {
            if (!DualPane) return;
            DualPane = false;

            // Focus has to come back to the surviving pane BEFORE the other one is hidden, or
            // every command would keep resolving through a collapsed pane. Collapsing it is the
            // slide's job now, at the END of the tween - hide it here and there is nothing left
            // on screen for the animation to move.
            FocusPane(LeftPane);

            ApplyPaneLayout(animate: true);
            UpdateTabBar();          // back to the surviving pane's own tab count
            UpdatePaneFocusRing();
        }

        // ═══════════════════════════════════════════════════════════
        //  ORIENTATION
        // ═══════════════════════════════════════════════════════════
        internal void TogglePaneOrientation()
        {
            _paneSideBySide = !_paneSideBySide;
            ApplyPaneLayout();
        }

        /// <summary>
        /// Point the second pane and the splitter at the column pair or the row pair, and zero
        /// the axis that is not in use. Both panes stay in the same host either way, so flipping
        /// orientation never rebuilds them and never costs a tab or a scroll position.
        /// </summary>
        private void ApplyPaneLayout(bool animate = false)
        {
            bool two = DualPane;
            bool cols = two && _paneSideBySide;
            bool rows = two && !_paneSideBySide;

            // Splitters: exactly one is live, and only while there are two panes.
            PaneSplitV.Visibility = cols ? Visibility.Visible : Visibility.Collapsed;
            PaneSplitH.Visibility = rows ? Visibility.Visible : Visibility.Collapsed;

            // Second pane sits in column 2 when side by side, row 2 when stacked.
            //
            // Keyed off ORIENTATION alone, never off whether the split is open. Reading it from
            // `cols` meant that closing - which turns cols false - yanked the pane out of the
            // column that was about to animate and dropped it into row 2, a track already at
            // zero. The pane vanished on the first frame and the tween then moved an empty
            // column, which is exactly what a close with no animation looks like. When the
            // split is shut the slot does not matter anyway: both tracks are zero.
            Grid.SetColumn(RightPane, _paneSideBySide ? 2 : 0);
            Grid.SetRow(RightPane,    _paneSideBySide ? 0 : 2);

            // A star width on an unused track would still claim space, so the idle axis goes to
            // zero rather than being left starred and hidden behind a collapsed child.
            //
            // The axis that is actually in use slides; the other is set flat. Which one slides
            // is decided by ORIENTATION, not by open/closed - closing a stacked split has to
            // animate the row, and animating the column there would move a track that is
            // already zero while the visible one snapped.
            bool slideCols = animate && _paneSideBySide;
            bool slideRows = animate && !_paneSideBySide;

            PaneColSplit.Width  = new GridLength(cols ? SplitPx : 0);
            PaneColB.MinWidth   = cols ? 180 : 0;
            if (slideCols) SlidePaneColumn(cols);
            else PaneColB.Width = cols ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            PaneRowSplit.Height = new GridLength(rows ? SplitPx : 0);
            PaneRowB.MinHeight  = rows ? 120 : 0;
            if (slideRows) SlidePaneRow(rows);
            else PaneRowB.Height = rows ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            // Reset the first track to an even split whenever the pairing changes; a drag from a
            // previous session's orientation would otherwise carry over as a lopsided start.
            PaneColA.Width  = new GridLength(1, GridUnitType.Star);
            PaneRowA.Height = new GridLength(1, GridUnitType.Star);

            // The rail button accents while the split is open, the same tell the search and
            // bookmarks toggles use (RailButton keys off Tag="on").
            DualPaneRailBtn.Tag = two ? "on" : null;

            ApplyPaneMargins();
        }

        // ═══════════════════════════════════════════════════════════
        //  OPEN / CLOSE SLIDE
        // ═══════════════════════════════════════════════════════════
        // Same motion as the sidebar (PanelSlide.cs): 160ms on the track, easing OUT on the way
        // open and IN on the way closed. Not the same CODE, because the two are not the same
        // shape. A sidebar opens to a REMEMBERED PIXEL WIDTH and stays there; the second pane
        // opens to a SHARE of the window and has to keep that share when the window resizes,
        // which is what a star length is for and what an animation cannot express.
        //
        // So the tween runs in pixels and the landing is a star. The intermediate frames are
        // pixel widths that happen to end on the even split; the moment the tween lands the
        // track becomes 1*, and from then on the divider stays put through a window resize.
        private const int PaneSlideMs = 160;

        // A pane must NOT re-lay-out while its track moves. Left to reflow, the results view
        // re-measures on every frame and the tile panel virtualizes columns away as the width
        // drops, so the icons blank out long before the pane has finished leaving - which reads
        // as the contents dying rather than the pane sliding. Freezing the pane at its full size
        // and pinning it to its own edge means the moving track edge WIPES it instead, and the
        // contents are never asked to relayout at all. Same trick PanelSlide.cs uses.
        //
        // PaneHost has to clip while that is true: a Grid does not clip its children, so a pane
        // frozen wider than its column would hang over the other one instead of being cut off.
        private void FreezePane(bool horizontal, double size)
        {
            PaneHost.ClipToBounds = true;

            if (horizontal)
            {
                // Right-hand pane, so it stays glued to the RIGHT edge and is revealed or eaten
                // by its left edge - the mirror of the sidebar staying glued to the left.
                RightPane.HorizontalAlignment = HorizontalAlignment.Right;
                RightPane.Width = size;
            }
            else
            {
                RightPane.VerticalAlignment = VerticalAlignment.Bottom;
                RightPane.Height = size;
            }
        }

        private void ThawPane()
        {
            RightPane.ClearValue(FrameworkElement.WidthProperty);
            RightPane.ClearValue(FrameworkElement.HeightProperty);
            RightPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            RightPane.VerticalAlignment   = VerticalAlignment.Stretch;
            PaneHost.ClipToBounds = false;
        }

        private void SlidePaneColumn(bool open)
        {
            // Half of what the pair will share once the splitter has taken its cut. Read off
            // pane A because it is the one currently holding the whole width.
            double target = open ? System.Math.Max(180, (PaneColA.ActualWidth - SplitPx) / 2) : 0;
            double from   = PaneColB.ActualWidth;

            // On OPEN the pane was only just un-collapsed, so its ActualWidth is still 0 and the
            // target stands in as the size to freeze at.
            FreezePane(horizontal: true,
                       size: RightPane.ActualWidth > 8 ? RightPane.ActualWidth : target);

            // MinWidth outranks Width in a Grid, so a track pinned at 180 cannot be tweened
            // down to nothing. Released for the tween, restored when it lands.
            PaneColB.MinWidth = 0;

            SlideTrack(PaneColB, ColumnDefinition.WidthProperty, from, target, open,
                       settle: () =>
                       {
                           ThawPane();
                           PaneColB.MinWidth = open ? 180 : 0;
                           PaneColB.Width = open
                               ? new GridLength(1, GridUnitType.Star)
                               : new GridLength(0);
                       });
        }

        private void SlidePaneRow(bool open)
        {
            double target = open ? System.Math.Max(120, (PaneRowA.ActualHeight - SplitPx) / 2) : 0;
            double from   = PaneRowB.ActualHeight;

            FreezePane(horizontal: false,
                       size: RightPane.ActualHeight > 8 ? RightPane.ActualHeight : target);

            PaneRowB.MinHeight = 0;

            SlideTrack(PaneRowB, RowDefinition.HeightProperty, from, target, open,
                       settle: () =>
                       {
                           ThawPane();
                           PaneRowB.MinHeight = open ? 120 : 0;
                           PaneRowB.Height = open
                               ? new GridLength(1, GridUnitType.Star)
                               : new GridLength(0);
                       });
        }

        /// <summary>
        /// Tween one grid track between two pixel sizes, then hand it to <paramref name="settle"/>
        /// to take its final value. Works on either axis because GridLength is GridLength.
        /// </summary>
        private void SlideTrack(System.Windows.Media.Animation.IAnimatable track,
                                DependencyProperty prop,
                                double from, double to, bool open, System.Action settle)
        {
            var anim = new GridLengthAnimation
            {
                From = from,
                To   = to,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(PaneSlideMs)),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = open
                        ? System.Windows.Media.Animation.EasingMode.EaseOut
                        : System.Windows.Media.Animation.EasingMode.EaseIn,
                },
            };

            anim.Completed += (_, _) =>
            {
                // Clear FIRST: an animation left running holds the property at its final value
                // and every later write is silently ignored, which would freeze the split at
                // whatever pixel width it happened to land on.
                track.BeginAnimation(prop, null);
                settle();

                // Collapsing the pane is the last thing that happens, not the first: it stays
                // on screen for the whole close so there is something to watch move.
                if (!open) RightPane.Visibility = Visibility.Collapsed;

                // The margins depend on the split being open, and ApplyPaneMargins already ran
                // with the final answer - but the ring and the tab edges are drawn against a
                // pane that has only now reached its real size.
                UpdatePaneFocusRing();
            };

            track.BeginAnimation(prop, anim);
        }

        /// <summary>
        /// Each pane's margins, from one place. Both panes are set every time rather than just
        /// the focused one: this used to run through <c>Pane</c> from ApplySearchPanel, so in
        /// dual pane only whichever half had focus ever had its margin corrected.
        /// </summary>
        internal void ApplyPaneMargins()
        {
            // An open search panel butts straight against the pane it sits beside, so the edge
            // margin goes with it - there is no window edge there any more either.
            double edge = _searchOpen ? 0 : PaneEdge;      // SearchPanel.cs
            bool cols = DualPane && _paneSideBySide;

            SetPaneMargin(LeftPane, cols ? PaneGutter : edge);
            SetPaneMargin(RightPane, edge);
        }

        // The -1 top tucks the pane's top border under the tab strip, so the active tab and the
        // pane read as one surface rather than being divided by a hairline.
        //
        // ONLY while there IS a strip. A collapsed element contributes no height and no margin,
        // so with one tab the row above is zero high and -1 puts the pane's top border one pixel
        // outside the control, where it is clipped away - the ring came out open along its top
        // edge. Nothing to tuck under means nothing to pull up by.
        private static void SetPaneMargin(FilePane p, double right)
        {
            double top = p.TabBar.Visibility == Visibility.Visible ? -1 : 0;

            p.ResultsPane.Margin  = new Thickness(0, top, right, 0);
            p.TabFadeGhost.Margin = p.ResultsPane.Margin;
            p.TabBar.Margin       = new Thickness(0, 6, right, 0);
        }

        // ═══════════════════════════════════════════════════════════
        //  FOCUS RING
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Mark the focused pane. Only meaningful with two panes open - with one, the ring would
        /// be decoration on the only thing you could possibly be acting on, so it is cleared.
        /// The border brush moves rather than the thickness, so nothing reflows as focus moves.
        /// </summary>
        internal void UpdatePaneFocusRing()
        {
            foreach (var p in new[] { LeftPane, RightPane })
            {
                bool lit = DualPane && ReferenceEquals(p, Pane);

                p.ResultsPane.SetResourceReference(Border.BorderBrushProperty,
                    lit ? "PrimaryBrush" : "PaneBorderBrush");

                // The ring line in the band is that same border continuing across the top of
                // the pane, so it takes the same brush. It is a child of the band rather than a
                // border on the band, which is what lets the active tab break it (FilePane.xaml).
                p.TabBarRing.SetResourceReference(Border.BorderBrushProperty,
                    lit ? "PrimaryBrush" : "PaneBorderBrush");

                // And the active tab's own sides, via the model so the template can trigger on
                // it. PaneDimmed is the other half: the active tab of the pane that does NOT
                // have focus drops its lip to the dimmed accent. Deliberately not !PaneFocused -
                // with one pane open both are false and that pane's lip stays bright.
                foreach (var t in p.Tabs)
                {
                    t.PaneFocused = lit && t.IsActive;
                    t.PaneDimmed  = DualPane && !lit && t.IsActive;
                }

                // The outermost verticals come from the band, not from the tab - a first or last
                // tab's own side border lands on the strip's clip edge and gets cut
                // (FilePane.xaml). Same ownership rule the pane's corner rounding uses: the
                // first tab owns the left edge, the last owns the right, and only while active.
                // Read off the tab rather than recomputed from the collection: with the strip
                // windowed (Tabs.cs ApplyTabWindow) the tab on an edge is not the one at the end
                // of the list, and two places working that out separately is two places to get
                // it wrong. UpdateTabBarInPane sets them, and every path here runs after it.
                bool firstActive = p.Active?.IsFirst == true;
                bool lastActive  = p.Active?.IsLast  == true;
                p.TabEdgeLeft.Visibility  = lit && firstActive ? Visibility.Visible : Visibility.Collapsed;
                p.TabEdgeRight.Visibility = lit && lastActive  ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  PER-PANE TAB HELPERS
        // ═══════════════════════════════════════════════════════════
        // CreateTab/ActivateTab in Tabs.cs act on the FOCUSED pane, which is exactly wrong while
        // seeding a pane that does not have focus yet. This borrows focus for the call and hands
        // it straight back, so seeding runs through Tabs.cs's own code path rather than a second
        // copy of it that could drift.
        //
        // It has to be ASYNC and hold focus across the await. NavigateTo is `async Task` and
        // writes Pane.RootPathBox / ScopePathLabel / ResultsHeader AFTER it resumes - so handing
        // focus back synchronously would let the second pane's folder listing land in the first
        // pane. The tab is therefore created NOT browsing (so ActivateTab's own fire-and-forget
        // `_ = NavigateTo(...)` never triggers) and the navigation is done here, awaited, with
        // focus still pointed at the target pane.
        private async void SeedPane(FilePane pane, string? folder)
        {
            var keep = Pane;
            FocusPaneQuiet(pane);
            try
            {
                var t = CreateTab();       // Tabs.cs - not browsing yet, so no async nav fires
                ActivateTab(t);            // Tabs.cs - wires the pane's lists at this focus

                if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
                    await NavigateTo(folder!, record: true);   // Browse.cs - awaited, focus held
            }
            finally
            {
                FocusPaneQuiet(keep);
            }

            // The window-wide view settings have to be pushed into the new pane, or it comes up
            // on its XAML default template with its view buttons unlit. Both of these walk every
            // live pane themselves (Panes.cs ForEachPane), so one call covers both panes.
            ApplyResultsView();          // ResultsView.cs - view mode, buttons, details header
            UpdateViewOptionButtons();   // ViewOptions.cs - show hidden, folders on top

            UpdatePaneFocusRing();
        }
    }
}
