using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        /// <summary>Minimum width for EITHER pane A or pane B (2026-08-02 resize rework). 300 is
        /// enough for a details-view file listing (name + a size/date column or two) without
        /// crushing into an icon soup; the old 180 predates the "each pane has a minimum width"
        /// requirement and was really just "don't let it hit zero". Applies to the side-by-side
        /// (column) case only - stacked/row orientation keeps its own 120 MinHeight, out of scope
        /// of this change (Steve's spec was about width: the gutter and the window's right edge).</summary>
        private const double PaneMinWidth = 300;

        /// <summary>Pixels the WINDOW itself has grown because of the dual-pane split (F10 open in
        /// floating mode, plus any gutter drag while floating). CloseSecondPane gives back exactly
        /// this much so the window doesn't end up oversized once pane A is alone again.</summary>
        private double _paneWindowGrowth;

        /// <summary>True once the window has been grown for the current dual-pane session (as
        /// opposed to split-in-place, where pane A shrank to make room and the window never
        /// changed size). Tells CloseSecondPane whether there is anything to give back.</summary>
        private bool _paneGrewWindow;

        /// <summary>Decided fresh at the start of every gutter drag (WindowState can change without
        /// the pane closing and reopening): floating -&gt; the drag grows/shrinks the WINDOW and
        /// pane B's width never moves; maximized/snapped -&gt; there is no room to grow, so the
        /// drag trades space between A and B like an ordinary split.</summary>
        private bool _paneGutterGrowsWindow;

        /// <summary>Pane A's width as of the last DragDelta tick, so PaneSplitV_DragDelta can read
        /// what GridSplitter actually did (which may be less than the raw drag delta, clamped by
        /// A's own MinWidth) instead of trusting the raw mouse delta and overshooting.</summary>
        private double _gutterAWidthAtLastDelta;

        /// <summary>This window's own MinWidth before any dual-pane minimum was layered on top,
        /// captured once so it can be restored exactly when the split closes.</summary>
        private double _paneSingleMinWidth = -1;

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

            // Opening the column split, animated - the one case where pane A's width must NOT be
            // reset below (SlidePaneColumn/SlidePaneColumnGrow own setting it, to whatever pixel
            // width they land on). Every other call here (close, orientation flip, a non-animated
            // re-entry into columns) still wants the plain reset.
            bool openingCols = slideCols && cols;

            // The window's own OS-level minimum has to grow with the split, or the native
            // WM_GETMINMAXINFO clamp (Chrome.cs WmGetMinMaxInfo, which reads this.MinWidth) would
            // let the frame get dragged narrower than A's + B's own MinWidths and start crushing
            // one of them. Captured once so it can be restored exactly when the split closes.
            if (_paneSingleMinWidth < 0) _paneSingleMinWidth = MinWidth;
            MinWidth = cols ? System.Math.Max(_paneSingleMinWidth, PaneMinWidth * 2 + SplitPx)
                             : _paneSingleMinWidth;

            PaneColSplit.Width  = new GridLength(cols ? SplitPx : 0);
            PaneColB.MinWidth   = cols ? PaneMinWidth : 0;
            if (slideCols) SlidePaneColumn(cols);
            else PaneColB.Width = cols ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            PaneRowSplit.Height = new GridLength(rows ? SplitPx : 0);
            PaneRowB.MinHeight  = rows ? 120 : 0;
            if (slideRows) SlidePaneRow(rows);
            else PaneRowB.Height = rows ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            // Reset the first track to an even split whenever the pairing changes; a drag from a
            // previous session's orientation would otherwise carry over as a lopsided start.
            //
            // NOT while opening the column split (openingCols): pane A must keep its EXACT current
            // width there (Steve, 2026-08-02 - "hitting f10 slides out a second pane... without
            // changing the size of the first pane"). SlidePaneColumn/SlidePaneColumnGrow set A's
            // final pixel width themselves; resetting it to star here would stomp that a moment
            // before the tween even starts.
            if (!openingCols) PaneColA.Width = new GridLength(1, GridUnitType.Star);
            PaneRowA.Height = new GridLength(1, GridUnitType.Star);

            // The rail button accents while the split is open, the same tell the search and
            // bookmarks toggles use (RailButton keys off Tag="on").
            DualPaneRailBtn.Tag = two ? "on" : null;

            // The dedicated ClosePane/OpenPane glyph pair (Segoe MDL2 codepoints 0xE89F and
            // 0xE8A0), which Steve picked specifically for this button (2026-08-02) rather than
            // a chevron that also tracks orientation: 0xE89F while closed (clicking opens it),
            // 0xE8A0 while open (clicking closes it). Built from (char) casts and never typed as
            // a literal PUA character - literal glyphs do not survive tooling (family-wide rule;
            // the codepoint is right there in the hex if this ever needs checking against
            // Character Map).
            int glyph = two ? 0xE8A0 : 0xE89F;
            DualPaneRailBtn.Content = ((char)glyph).ToString();

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

        // Steve, 2026-08-02: "hitting f10 slides out a second pane from the first pane without
        // changing the size of the first pane. thats the behavior if the window is not maximized
        // or snapped. if its maximized or snapped, then yeah hitting f10 should split the full
        // area into two panes." Two branches, one per case; both leave pane A a literal PIXEL
        // column and pane B a STAR column once they land, so the far-right window edge (an
        // ordinary WPF Grid resize, no code needed) and the gutter (PaneSplitV_DragDelta below)
        // only ever move pane B afterward, regardless of which branch opened the split.
        private void SlidePaneColumn(bool open)
        {
            if (open)
            {
                // B's own default/minimum width, plus the gutter it needs to sit in - what the
                // window would have to grow by to seat B alongside A without touching A at all.
                double growth = PaneMinWidth + SplitPx;
                bool canGrow = WindowState != WindowState.Maximized
                               && ActualWidth + growth <= MonitorWorkAreaWidthDip();

                if (canGrow)
                {
                    SlidePaneColumnGrow(growth);
                    return;
                }
                // Maximized, snapped, or simply no room on the current monitor: fall through to
                // the split-in-place branch below, same shape it has always had.
            }

            // Split-in-place: pane A shares its current width with B (on open), or gives it all
            // back (on close). Half of what the pair will share once the splitter has taken its
            // cut - read off pane A because it is the one currently holding the whole width.
            double target = open ? System.Math.Max(PaneMinWidth, (PaneColA.ActualWidth - SplitPx) / 2) : 0;
            double from    = PaneColB.ActualWidth;

            // On OPEN the pane was only just un-collapsed, so its ActualWidth is still 0 and the
            // target stands in as the size to freeze at.
            FreezePane(horizontal: true,
                       size: RightPane.ActualWidth > 8 ? RightPane.ActualWidth : target);

            // MinWidth outranks Width in a Grid, so a track pinned at PaneMinWidth cannot be
            // tweened down to nothing. Released for the tween, restored when it lands.
            PaneColB.MinWidth = 0;

            SlideTrack(PaneColB, ColumnDefinition.WidthProperty, from, target, open,
                       settle: () =>
                       {
                           ThawPane();

                           if (open)
                           {
                               // Pin A to whatever width it now holds. The invariant "A is a
                               // literal pixel column, B is star" has to hold no matter which
                               // branch opened the split, or the far-right-edge and gutter rules
                               // below would only work after an F10 that happened to grow the
                               // window, not one that split in place.
                               PaneColA.Width = new GridLength(PaneColA.ActualWidth);
                           }
                           else if (_paneGrewWindow)
                           {
                               // This close is undoing a GROW-branch open (or gutter drags that
                               // followed one) - give the window back exactly what it took.
                               Width -= _paneWindowGrowth;
                               _paneWindowGrowth = 0;
                               _paneGrewWindow = false;
                           }

                           if (!open) PaneColA.Width = new GridLength(1, GridUnitType.Star);
                           PaneColB.MinWidth = open ? PaneMinWidth : 0;
                           PaneColB.Width = open
                               ? new GridLength(1, GridUnitType.Star)
                               : new GridLength(0);
                       });
        }

        /// <summary>
        /// F10 opened on a floating (not maximized/snapped) window with room to spare: pane A
        /// keeps its EXACT current width, the window grows by <paramref name="growth"/> in one
        /// shot so pane B (star) has real leftover space to slide into, and B tweens in from 0 up
        /// to its share of that new space. Mirrors CloseSecondPane's job of giving the growth back
        /// (handled in SlidePaneColumn's own settle, since close always runs through there).
        /// </summary>
        private void SlidePaneColumnGrow(double growth)
        {
            // Captured and pinned FIRST, before the window resize below can trigger any layout
            // pass that might otherwise catch A mid-flight as still a star column.
            PaneColA.Width = new GridLength(PaneColA.ActualWidth);

            PaneColB.MinWidth = 0;   // released for the tween, restored in settle() below
            double from = PaneColB.ActualWidth;   // 0 - B starts collapsed
            double target = growth - SplitPx;     // B's own share once the gutter is subtracted

            FreezePane(horizontal: true, size: target);

            Width += growth;
            _paneWindowGrowth += growth;
            _paneGrewWindow = true;

            SlideTrack(PaneColB, ColumnDefinition.WidthProperty, from, target, true,
                       settle: () =>
                       {
                           ThawPane();
                           PaneColB.MinWidth = PaneMinWidth;
                           PaneColB.Width = new GridLength(1, GridUnitType.Star);
                       });
        }

        // ═══════════════════════════════════════════════════════════
        //  GUTTER DRAG (side-by-side only - PaneSplitV)
        // ═══════════════════════════════════════════════════════════
        // Steve, 2026-08-02: "when I drag the middle gutter it should resize pane a, pane b stays
        // the same size and just goes along for the ride." Pane B is a star column once the split
        // has landed (SlidePaneColumn/SlidePaneColumnGrow above), so it naturally keeps its own
        // rendered width whenever pane A's literal pixel width changes AND the window's total
        // width changes by that same amount - the window growing/shrinking in lockstep is what
        // makes "B stays the same size" literally true instead of B just eating the difference.
        //
        // Maximized/snapped windows have no room to grow, so there the drag falls back to an
        // ordinary PreviousAndNext trade between A and B - decided fresh at the start of every
        // drag (DragStarted), because WindowState can change without the split closing and
        // reopening (maximize the window mid-session, or un-maximize it).
        //
        // GridResizeBehavior has no "resize only the previous column" value - PreviousAndNext is
        // the closest built-in option, but since PaneSplitV sits in its own dedicated column
        // (Previous = PaneColA, Next = PaneColB), the splitter's own MoveSplitter logic converts
        // BOTH columns to explicit pixel GridLengths on every tick, which would silently strip
        // PaneColB back out of Star sizing. So PreviousAndNext is used in both modes (there is no
        // narrower option to reach for), and the floating-mode branch below undoes that side
        // effect on PaneColB every tick, right after growing the window to match whatever A
        // actually moved - restoring the Pixel/Star invariant the rest of this class depends on.
        private void PaneSplitV_DragStarted(object sender, DragStartedEventArgs e)
        {
            _paneGutterGrowsWindow = WindowState != WindowState.Maximized;
            PaneSplitV.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            _gutterAWidthAtLastDelta = PaneColA.ActualWidth;
        }

        private void PaneSplitV_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // Maximized/snapped: PreviousAndNext already traded space between A and B above: B is
            // allowed to change here, so there is nothing left for this handler to do.
            if (!_paneGutterGrowsWindow) return;

            // GridSplitter (ResizeBehavior=PreviousAndNext) has already resized PaneColA by the
            // time this fires. Reading what it actually did, rather than trusting
            // e.HorizontalChange, means a drag clamped by A's own MinWidth doesn't grow the
            // window by more than A actually moved.
            double actualDelta = PaneColA.ActualWidth - _gutterAWidthAtLastDelta;
            if (actualDelta == 0) return;

            // Clamp the window's own growth/shrink to the monitor's work area and to the window's
            // floor (MinWidth, already bumped for the open split in ApplyPaneLayout). If the raw
            // delta would cross either line, pull pane A back to match so it never silently
            // outruns what the window is actually allowed to become.
            double maxWidth = MonitorWorkAreaWidthDip();
            double newWidth = Width + actualDelta;
            double clampedWidth = System.Math.Max(MinWidth, System.Math.Min(maxWidth, newWidth));
            if (clampedWidth != newWidth)
            {
                double allowed = clampedWidth - Width;
                PaneColA.Width = new GridLength(_gutterAWidthAtLastDelta + allowed);
                actualDelta = allowed;
            }

            Width += actualDelta;
            _paneWindowGrowth += actualDelta;
            _paneGrewWindow = true;
            _gutterAWidthAtLastDelta = PaneColA.ActualWidth;

            // PreviousAndNext just converted PaneColB from Star to an explicit pixel GridLength
            // as a side effect of resizing PaneColA - undo that every tick, now that the window
            // has grown/shrunk to match. With PaneColB back on Star, it renders at exactly
            // "whatever's left of the window" - the same width it had before this tick, since A's
            // pixel width and the window's total width moved together.
            PaneColB.Width = new GridLength(1, GridUnitType.Star);
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
            ForEachPane(ApplyDetailsPaneInPaneNoAnim);   // DetailsPane.cs - height (and preview width) too

            UpdatePaneFocusRing();
        }
    }
}
