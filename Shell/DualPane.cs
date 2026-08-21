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
        /// of this change (the spec was about width: the gutter and the window's right edge).</summary>
        private const double PaneMinWidth = 300;

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

        /// <summary>Pane A's share of the split at the last settled layout. Window-state JUMPS
        /// (snap, maximize, unmaximize) re-derive A's pixel width from this so the panes keep
        /// their proportions - a 50/50 split must not come out of maximize as 75/25. An
        /// interactive edge drag keeps A fixed so the window edge eats pane B only; WM_ENTER/
        /// EXITSIZEMOVE (<see cref="_inWindowSizeMove"/>, tracked in Chrome.cs WndProc) tells
        /// the two apart. Ported from KillerPDF 1.7.4 (Shell/SplitPane.cs).</summary>
        private double _paneARatio;

        /// <summary>True while the user is interactively moving or resizing the window
        /// (WM_ENTERSIZEMOVE..WM_EXITSIZEMOVE, tracked in Chrome.cs WndProc).</summary>
        private bool _inWindowSizeMove;

        /// <summary>True between the gutter's DragStarted and DragCompleted, so the state-jump
        /// path never fights a drag in progress.</summary>
        private bool _paneGutterDragging;

        private bool _paneRatioHooked;

        /// <summary>One-time wiring for the ratio-keeping resize path. Queued, never run inline:
        /// writing a column width from inside the layout pass that raised SizeChanged re-enters
        /// it (same rule as KillerPDF's SplitHost handler).</summary>
        private void HookPaneRatio()
        {
            if (_paneRatioHooked) return;
            _paneRatioHooked = true;
            PaneHost.SizeChanged += (_, _) =>
                Dispatcher.BeginInvoke(new System.Action(OnPaneHostResized),
                                       System.Windows.Threading.DispatcherPriority.Background);
            PaneSplitV.DragCompleted += PaneSplitV_DragCompleted;
        }

        /// <summary>PaneHost.SizeChanged lands here (queued). A size change OUTSIDE an
        /// interactive move/resize is a window-state jump - snap, maximize, unmaximize - and
        /// those keep the panes' RATIO; an edge drag keeps pane A fixed so the window edge eats
        /// pane B only (the Pixel/Star invariant already does that on its own).</summary>
        private void OnPaneHostResized()
        {
            if (!DualPane || !_paneSideBySide) return;
            if (_paneSlideSettle != null || _paneGutterDragging) return;
            double avail = PaneHost.ActualWidth - SplitPx;
            if (avail <= 0) return;

            if (_inWindowSizeMove || _paneARatio <= 0)
            {
                // Edge drag (or nothing remembered yet): A stays fixed; just refresh the share
                // this layout settled on, so the next state jump restores it.
                RememberPaneRatio();
                return;
            }

            double aW = System.Math.Max(PaneMinWidth, avail * _paneARatio);
            aW = System.Math.Min(aW, System.Math.Max(PaneMinWidth, avail - PaneMinWidth));
            if (!PaneColA.Width.IsStar && System.Math.Abs(PaneColA.ActualWidth - aW) > 0.5)
                PaneColA.Width = new GridLength(aW);
        }

        /// <summary>Remembers the proportion the current layout settled on - the value the
        /// state-jump path restores. Refreshed after a gutter drag, a slide landing, and every
        /// interactive edge resize.</summary>
        private void RememberPaneRatio()
        {
            if (!DualPane || !_paneSideBySide) return;
            double avail = PaneHost.ActualWidth - SplitPx;
            if (avail > 0 && PaneColA.ActualWidth > 0)
                _paneARatio = PaneColA.ActualWidth / avail;
        }

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
            HookPaneRatio();

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
                // restoreFocus:false - SeedPane would otherwise quietly hand focus BACK to
                // LeftPane once its awaited navigation lands, which is what "opens but stays on
                // the pane I was already in" was actually caused by. FocusPane below is called
                // synchronously right after, while SeedPane's own quiet focus-set (before its
                // first await) is still in effect, so this both finishes the tab activation
                // properly AND is not later undone by SeedPane's finally.
                SeedPane(RightPane, folder, restoreFocus: false);
                FocusPane(RightPane);
            }
            else
            {
                FocusPane(RightPane);
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
            // NOT during ANY animated column slide - open OR close (slideCols,
            // fixed to also cover close, having only ever covered open). SlidePaneColumn owns
            // PaneColA.Width for the whole tween in both directions: pinned to its captured pixel
            // width throughout, only flipped to Star in its own settle() once B has actually
            // finished moving. Marking A Star here too, synchronously and a moment before that
            // tween starts, put A into the SAME star track as B for the entire animation - so
            // Grid's live layout grew A in lockstep with every frame of B's animated shrink,
            // reading as A visibly stretching WHILE the pane closed instead of B just sliding away
            // and A snapping to reclaim the space only once B was actually gone - a weird
            // grow/stretch animation before closing, as if the animation ran the wrong
            // way. Every other call here (orientation flip, a non-animated re-entry into
            // columns) still wants the plain reset.
            if (!slideCols) PaneColA.Width = new GridLength(1, GridUnitType.Star);
            PaneRowA.Height = new GridLength(1, GridUnitType.Star);

            // The rail button accents while the split is open, the same tell the search and
            // bookmarks toggles use (RailButton keys off Tag="on").
            DualPaneRailBtn.Tag = two ? "on" : null;

            // The dedicated ClosePane/OpenPane glyph pair (Segoe MDL2 codepoints 0xE89F and
            // 0xE8A0), picked specifically for this button rather than
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

        // F10 slides out a second pane from the first pane without changing the size of the
        // first pane when the window is not maximized or snapped; when it IS maximized or
        // snapped, F10 splits the full
        // area into two panes. Two branches, one per case; both leave pane A a literal PIXEL
        // column and pane B a STAR column once they land, so the far-right window edge (an
        // ordinary WPF Grid resize, no code needed) and the gutter (PaneSplitV_DragDelta below)
        // only ever move pane B afterward, regardless of which branch opened the split.
        private void SlidePaneColumn(bool open)
        {
            // Flushed FIRST, before anything below reads Width or starts a fresh
            // AnimateWindowWidth - a still-pending toggle's own settle() (see
            // FinishPendingPaneSlide's remark) touches both, and starting a fresh animation that
            // the old settle then clobbers a moment later produced exactly the close stretching
            // out instead of closing - a second F10 fired close enough behind the first that this
            // method's own later `FinishPendingPaneSlide()` call (buried inside SlideTrack) ran
            // too late to matter; by then this method had already computed its own width target
            // off not-yet-corrected numbers and already kicked off its own animation.
            FinishPendingPaneSlide();

            if (open)
            {
                // The pane used to always open at its bare MINIMUM width (PaneMinWidth, 300)
                // regardless of how much room there actually was, so it always popped out
                // skinny. Aim instead for something comfortably sized
                // relative to pane A's own current width (40%, capped so an ultrawide monitor
                // does not open a needlessly huge second pane), then pull that back down - never
                // below PaneMinWidth - to whatever room the monitor actually has. Falls back to
                // split-in-place only if even the bare minimum would not fit.
                const double desiredRatio = 0.4;
                const double desiredCap = 600;
                double desiredWidth = System.Math.Min(desiredCap, PaneColA.ActualWidth * desiredRatio);
                // MonitorRoomToGrowRightDip, not MonitorWorkAreaWidthDip (F10
                // slid the second pane clean off screen on a window snapped to the right half of
                // the monitor) - the width check alone says nothing about where the window's
                // LEFT edge already sits, and growth happens in place, so a window already
                // parked against the monitor's right edge has nowhere left to grow into even
                // though its own width comfortably clears the monitor's total work width.
                double room = MonitorRoomToGrowRightDip();
                bool canGrow = WindowState != WindowState.Maximized
                               && room >= PaneMinWidth + SplitPx;

                if (canGrow)
                {
                    double width = System.Math.Max(PaneMinWidth,
                                                     System.Math.Min(desiredWidth, room - SplitPx));
                    SlidePaneColumnGrow(width + SplitPx);
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

            // CLOSE: pane A must NEVER visibly change size, full stop - pane A stays where it
            // is and pane B slides closed; the background must not snap instantly while only
            // the pane animates, and pane A must never stretch to the full width on the way out.
            // Earlier attempts here tried to distinguish "did the ORIGINAL open grow the window,
            // or did it split A's own space in place" and only shrink the window back in the
            // first case, letting A visibly reclaim the space in the second (however smoothly
            // animated) - which is exactly the behavior just rejected. The window instead gives
            // back EXACTLY what B currently occupies, always, regardless of how the split was
            // originally opened: shrinking a window is never blocked by monitor room the way
            // GROWING one is (that constraint is what MonitorRoomToGrowRightDip above is for),
            // so there is nothing to fall back from - except one real exception, a MAXIMIZED
            // window, whose Width cannot be set independently of its maximized state at all, so
            // there A has no choice but to grow back into the space (animateAReclaim below).
            //
            // This also drops the old running "_paneWindowGrowth" tally in favor of reading
            // pane B's OWN current width fresh right here - a plain, always-correct number,
            // rather than a cumulative figure nudged by every open and every gutter drag, which
            // is exactly the kind of bookkeeping that produced the earlier "closes repeatedly
            // stretch it further" bug (interrupted toggles leaving it stale).
            bool canShrinkWindow = WindowState != WindowState.Maximized;
            double closeWidthTarget = Width - (from + SplitPx);
            if (!open && canShrinkWindow) AnimateWindowWidth(closeWidthTarget);

            // Maximized only: A has no window to shrink into, so it grows back into B's space
            // instead, in lockstep with B's own collapse below rather than a live Star column
            // (recomputed off B's CURRENT rendered value every frame, which reads as A jumping a
            // beat ahead of B actually moving) or a pin-then-pop (freezing A for the whole tween
            // and only handing it back in one frame at the very end, which is what produced the
            // "opposite of what it should do" complaint above in the first place).
            bool animateAReclaim = !open && !canShrinkWindow;
            if (animateAReclaim)
            {
                var aAnim = new GridLengthAnimation
                {
                    From = PaneColA.ActualWidth,
                    To   = PaneColA.ActualWidth + from + SplitPx,   // absorbs B's width + the vanishing gutter
                    Duration = new Duration(System.TimeSpan.FromMilliseconds(PaneSlideMs)),
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn,
                    },
                };
                PaneColA.BeginAnimation(ColumnDefinition.WidthProperty, aAnim);
            }

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
                           else if (canShrinkWindow)
                           {
                               // Clear the animation and land on the exact value - same reasoning
                               // as SlideTrack's own "clear first" comment: a running animation
                               // holds the property at its own final value and ignores a plain
                               // assignment made while it is still attached.
                               BeginAnimation(FrameworkElement.WidthProperty, null);
                               Width = closeWidthTarget;
                           }

                           if (!open)
                           {
                               // Clear A's own reclaim animation (if animateAReclaim started one)
                               // before handing it to Star - same "clear first" reasoning as
                               // everywhere else here: a running animation ignores a plain
                               // assignment made while it is still attached.
                               PaneColA.BeginAnimation(ColumnDefinition.WidthProperty, null);
                               PaneColA.Width = new GridLength(1, GridUnitType.Star);
                           }
                           PaneColB.MinWidth = open ? PaneMinWidth : 0;
                           PaneColB.Width = open
                               ? new GridLength(1, GridUnitType.Star)
                               : new GridLength(0);
                           // Queued so the ratio reads the settled layout, not this frame's.
                           Dispatcher.BeginInvoke(new System.Action(RememberPaneRatio),
                               System.Windows.Threading.DispatcherPriority.Background);
                       });
        }

        /// <summary>
        /// Animates the WINDOW's own Width to <paramref name="to"/>, on the same duration and
        /// easing shape as the pane-slide it accompanies (PaneSlideMs), so the window's own
        /// background/chrome grows or shrinks in step with the pane instead of snapping to its
        /// final size in one frame while only the pane's frozen content visibly slides.
        /// </summary>
        private void AnimateWindowWidth(double to)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(PaneSlideMs)),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = to > Width
                        ? System.Windows.Media.Animation.EasingMode.EaseOut
                        : System.Windows.Media.Animation.EasingMode.EaseIn,
                },
            };
            BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        /// <summary>
        /// F10 opened on a floating (not maximized/snapped) window with room to spare: pane A
        /// keeps its EXACT current width, the window grows by <paramref name="growth"/> in step
        /// with pane B's own tween (AnimateWindowWidth) so pane B (star) has real leftover space
        /// to slide into, and B tweens in from 0 up to its share of that new space. Closing always
        /// shrinks the window back by exactly whatever B currently occupies (SlidePaneColumn's own
        /// close branch), reading pane B's live width fresh rather than tracking a running total
        /// here - so nothing needs recording on the way in.
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

            // Animated rather than an instant `Width += growth` (see
            // AnimateWindowWidth's own remark): a snap here left the window's own background/
            // chrome already at full size the instant the split started, with only the frozen
            // pane's reveal-wipe visibly animating on top of it.
            AnimateWindowWidth(Width + growth);

            SlideTrack(PaneColB, ColumnDefinition.WidthProperty, from, target, true,
                       settle: () =>
                       {
                           ThawPane();
                           PaneColB.MinWidth = PaneMinWidth;
                           PaneColB.Width = new GridLength(1, GridUnitType.Star);
                           // Queued so the ratio reads the settled layout, not this frame's.
                           Dispatcher.BeginInvoke(new System.Action(RememberPaneRatio),
                               System.Windows.Threading.DispatcherPriority.Background);
                       });
        }

        // ═══════════════════════════════════════════════════════════
        //  GUTTER DRAG (side-by-side only - PaneSplitV)
        // ═══════════════════════════════════════════════════════════
        // Dragging the middle gutter resizes pane A; pane B stays the same size and just goes
        // along for the ride. Pane B is a star column once the split
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
        // "Snapped" is NOT WindowState.Maximized - Aero-snapping a window (Win+Left/Right) leaves
        // it at WindowState.Normal, just repositioned/resized to a screen half, so the plain
        // `WindowState != Maximized` check below used to wave a snapped window through as
        // "floating with room to grow" (on a snapped window, dragging the center divider left
        // tried to SHRINK the window to keep B "the same size", which just
        // pulled the window's own right edge in off the screen edge it was snapped against,
        // leaving a gap between the window and the monitor's true right edge instead of B staying
        // flush with it). Same class of bug as the F10 fix above (MonitorRoomToGrowRightDip,
        // Chrome.cs) and the same fix: a window with no real room to grow right - snapped flush
        // against it, same as maximized - falls back to the same in-place trade the maximized
        // case already used, for the whole drag.
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
            _paneGutterDragging = true;
            _paneGutterGrowsWindow = WindowState != WindowState.Maximized
                                      && MonitorRoomToGrowRightDip() > 0;
            PaneSplitV.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            _gutterAWidthAtLastDelta = PaneColA.ActualWidth;
        }

        private void PaneSplitV_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _paneGutterDragging = false;
            // The maximized/snapped PreviousAndNext trade leaves BOTH columns as pixel lengths;
            // restore the Pixel/Star invariant (same rendered widths - star is the remainder)
            // so the edge-resize and state-jump rules keep working after the drag.
            if (DualPane && _paneSideBySide)
                PaneColB.Width = new GridLength(1, GridUnitType.Star);
            RememberPaneRatio();
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

            // Clamp the window's own growth/shrink to the REAL room available - how far the
            // window's right edge can move before it leaves the monitor (MonitorRoomToGrowRightDip,
            // not MonitorWorkAreaWidthDip's plain total-width check, same bug/fix pairing as the
            // F10 grow decision above) - and to the window's floor (MinWidth, already bumped for
            // the open split in ApplyPaneLayout). If the raw delta would cross either line, pull
            // pane A back to match so it never silently outruns what the window is actually
            // allowed to become.
            double maxWidth = Width + MonitorRoomToGrowRightDip();
            double newWidth = Width + actualDelta;
            double clampedWidth = System.Math.Max(MinWidth, System.Math.Min(maxWidth, newWidth));
            if (clampedWidth != newWidth)
            {
                double allowed = clampedWidth - Width;
                PaneColA.Width = new GridLength(_gutterAWidthAtLastDelta + allowed);
                actualDelta = allowed;
            }

            Width += actualDelta;
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
        /// Whatever the LAST SlideTrack call is still waiting to run once its tween lands - see
        /// FinishPendingPaneSlide.
        /// </summary>
        private System.Action? _paneSlideSettle;

        /// <summary>
        /// Forces a still-running pane-slide's landing logic to run NOW, out of band from its
        /// animation's own Completed event.
        ///
        /// BeginAnimation-ing the SAME DependencyProperty a second time silently replaces the
        /// running clock - the superseded animation's Completed event never fires (this is
        /// documented WPF behavior, not a bug in GridLengthAnimation). SlideTrack's settle()
        /// closure is where the real state lives (window-width giveback, MinWidth/Star restore,
        /// ThawPane) so skipping it on an interrupted tween left all of that stale. Pressing F10
        /// again before the 160ms open tween landed then started a SECOND SlideTrack before the
        /// FIRST one's own landing logic had ever run, uncorrected - repeat presses compound,
        /// each F10 stretching the pane further into the width the missing pane left behind.
        /// Called at the top of every new SlideTrack,
        /// so each toggle finishes cleanly before the next one is allowed to start.
        /// </summary>
        private void FinishPendingPaneSlide()
        {
            var pending = _paneSlideSettle;
            _paneSlideSettle = null;
            pending?.Invoke();
        }

        /// <summary>
        /// Tween one grid track between two pixel sizes, then hand it to <paramref name="settle"/>
        /// to take its final value. Works on either axis because GridLength is GridLength.
        /// </summary>
        private void SlideTrack(System.Windows.Media.Animation.IAnimatable track,
                                DependencyProperty prop,
                                double from, double to, bool open, System.Action settle)
        {
            FinishPendingPaneSlide();   // an interrupted earlier toggle lands before this one starts

            void FullSettle()
            {
                settle();

                // Collapsing the pane is the last thing that happens, not the first: it stays
                // on screen for the whole close so there is something to watch move.
                if (!open) RightPane.Visibility = Visibility.Collapsed;

                // The margins depend on the split being open, and ApplyPaneMargins already ran
                // with the final answer - but the ring and the tab edges are drawn against a
                // pane that has only now reached its real size.
                UpdatePaneFocusRing();
            }

            _paneSlideSettle = FullSettle;

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
                _paneSlideSettle = null;
                FullSettle();
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
            // The window-edge inset comes from PaneOuterMargin's RIGHT, not the PaneEdge constant.
            // 8 by default, which is what the constant was, but 0 on 98SE - a Win98 client area
            // fills its frame, and the hardcoded 8 read there as a too-fat gap along the right
            // edge. PaneGutter stays a constant: the gap BETWEEN two panes is
            // furniture, not a frame inset, and 98SE wants it just the same.
            var om = Application.Current?.TryFindResource("PaneOuterMargin") as Thickness?
                     ?? new Thickness(0, -1, PaneEdge, 0);
            double edge = _searchOpen ? 0 : om.Right;      // SearchPanel.cs
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
            // Top/left/bottom come from PaneOuterMargin and TabBarMargin, NOT from literals. The
            // -1 that used to be hardcoded here is what left a persistent line under the
            // active tab: PaneContent's page bevel is 2px of white across its top
            // (BevelLightThickness 2,2,0,0 on 98SE), the pane was pulled up only 1px against it, so
            // 1px of that white ran edge to edge under the tab. The tab cannot break a full-width
            // Border, so the pane has to ride far enough up for the tab's own opaque fill to cover
            // it - which is a per-theme number and therefore a token. 98SE states -2.
            // Only the RIGHT slot stays computed: it is the dual-pane gutter, decided by the
            // caller, not by the palette.
            var om = Application.Current?.TryFindResource("PaneOuterMargin") as Thickness?
                     ?? new Thickness(0, -1, 8, 0);
            var tm = Application.Current?.TryFindResource("TabBarMargin") as Thickness?
                     ?? new Thickness(0, 6, 8, 0);

            double top = p.TabBar.Visibility == Visibility.Visible ? om.Top : 0;

            p.ResultsPane.Margin  = new Thickness(om.Left, top, right, om.Bottom);
            p.TabFadeGhost.Margin = p.ResultsPane.Margin;
            p.TabBar.Margin       = new Thickness(tm.Left, tm.Top, right, tm.Bottom);
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

                // ResultsPane deliberately has no permanent bottom border: the ordinary content
                // edge follows the active scroller so it does not falsely mark the viewport as
                // the end of a long page. The focus ring is different UI state and must stay
                // unbroken, so its bottom segment is an independent overlay (FilePane.xaml).
                p.PaneFocusBottomEdge.Visibility = lit
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                // The ring line in the band is that same border continuing across the top of
                // the pane, so it takes the same brush. It is a child of the band rather than a
                // border on the band, which is what lets the active tab break it (FilePane.xaml).
                // TabRingIdleBrush, NOT PaneBorderBrush: it mirrors PaneBorderBrush on the twelve
                // rounded themes, but a flat theme states it transparent. Hardcoding the brush here
                // overrode the transparent PaneEdgeBrush the markup binds, which is what drew the
                // gray rule under the active tab and the gray stub at the left of the menu bar on
                // 98SE.
                // TabActiveRingBrush, not PrimaryBrush: it IS PrimaryBrush on every ordinary
                // theme, but 98SE states it Transparent - the lit ring was drawing the accent
                // across the top of the focused pane's band and down its sides on a theme whose
                // tabs carry no accent at all (dual pane).
                p.TabBarRing.SetResourceReference(Border.BorderBrushProperty,
                    lit ? "TabActiveRingBrush" : "TabRingIdleBrush");

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
                // Shown whenever the ACTIVE tab owns that edge, not only while lit: in single
                // pane (and on the unfocused half of a dual pane) the pane's own border should
                // continue up the active tab's outer side too, in the idle ring brush - the
                // rightmost active tab needs the pane border on its right edge, and the
                // leftmost active tab the same on its left.
                // TabRingIdleBrush mirrors PaneBorderBrush on the rounded themes and is
                // transparent on 98SE, so the flat theme stays exactly as it is.
                p.TabEdgeLeft.Visibility  = firstActive ? Visibility.Visible : Visibility.Collapsed;
                p.TabEdgeRight.Visibility = lastActive  ? Visibility.Visible : Visibility.Collapsed;
                p.TabEdgeLeft.SetResourceReference(Border.BackgroundProperty,
                    lit ? "TabActiveRingBrush" : "TabRingIdleBrush");
                p.TabEdgeRight.SetResourceReference(Border.BackgroundProperty,
                    lit ? "TabActiveRingBrush" : "TabRingIdleBrush");
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
        private async void SeedPane(FilePane pane, string? folder, bool restoreFocus = true)
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
                // OpenSecondPane wants the newly-opened pane to KEEP real command focus (F10
                // opening the second pane should leave that pane focused) rather
                // than have it quietly handed back to whichever pane had it before - so that one
                // caller passes restoreFocus:false and calls the real FocusPane itself right
                // after kicking this off. Every other use of this method keeps the original
                // quiet-restore behavior.
                if (restoreFocus) FocusPaneQuiet(keep);
            }

            // The window-wide view settings have to be pushed into the new pane, or it comes up
            // on its XAML default template with its view buttons unlit. Both of these walk every
            // live pane themselves (Panes.cs ForEachPane), so one call covers both panes.
            ApplyResultsView();          // ResultsView.cs - view mode, buttons, details header
            UpdateViewOptionButtons();   // ViewOptions.cs - show hidden, folders on top
            // Each pane's own open/height/user-sized state (restored per pane at startup,
            // DetailsPane.cs InitDetailsPane) still has to be pushed into the newly-live pane's
            // visuals once - it has never actually painted before now.
            foreach (var p in LivePanes()) ApplyDetailsPane(p, animate: false);

            UpdatePaneFocusRing();
        }
    }
}
