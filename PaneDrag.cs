using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Models;

// Moving a tab from one pane to the other. Partial of MainWindow.
//
// A tab is already self-contained: it owns its results, its history, its search config and,
// for a shell tab, a live pty. So a move is a move - take it out of one pane's collection and
// put it in the other's. Nothing is rebuilt, which is what lets a running command carry on
// through the drag without noticing.
//
// The reorder-within-a-pane drag lives in Tabs.cs and is untouched; this only takes over when
// the drop lands somewhere that pane is not.
namespace KillerShell
{
    public partial class MainWindow
    {
        /// <summary>
        /// The pane a drop landed on, or null when it landed on the pane it came from (or on
        /// nothing). Only ever returns the OTHER pane, so a drop inside the source pane still
        /// goes through the normal reorder path.
        /// </summary>
        private FilePane? DropTargetPane(MouseEventArgs e)
        {
            if (!DualPane) return null;

            var other = ReferenceEquals(Pane, LeftPane) ? RightPane : LeftPane;
            if (other.Visibility != Visibility.Visible) return null;

            // Generous on purpose: anywhere in the other pane counts, not just its tab strip.
            // A tab strip is a 24px target, and aiming for it while dragging is fussy - if you
            // let go over the other pane, you meant the other pane.
            var p = e.GetPosition(other);
            return p.X >= 0 && p.Y >= 0 && p.X <= other.ActualWidth && p.Y <= other.ActualHeight
                ? other
                : null;
        }

        // ═══════════════════════════════════════════════════════════
        //  DRAG FEEDBACK
        // ═══════════════════════════════════════════════════════════
        // Within a pane the REAL tab slides under the pointer, which is feedback enough. The
        // moment the pointer crosses into the other pane that stops working: the tab is still
        // parked in the strip it came from, and nothing follows the hand. So a ghost takes over
        // for the journey, and a caret shows where it would land.
        private bool _ghostShown;

        /// <summary>
        /// Called on every drag move. Shows the ghost while the pointer is over the other pane
        /// and hides it again the moment it comes home, so a drag that wanders out and back
        /// hands control cleanly to the in-strip reorder.
        /// </summary>
        private void UpdateDragFeedback(SearchTab t, MouseEventArgs e, FilePane? over)
        {
            if (over == null) { HideDragFeedback(); return; }

            if (!_ghostShown)
            {
                _ghostShown = true;
                TabDragGhostGlyph.Text = t.TabGlyph;
                TabDragGhostGlyph.Visibility = t.TabGlyph.Length > 0
                    ? Visibility.Visible : Visibility.Collapsed;
                TabDragGhostText.Text = t.Title;
                DragLayer.Visibility = Visibility.Visible;
                Anim.FadeIn(DragLayer);          // Anim.cs
            }

            // Positioned by the same grab offset the in-strip drag uses, so the ghost sits
            // under the pointer exactly where the tab did when it was picked up.
            var p = e.GetPosition(DragLayer);
            Canvas.SetLeft(TabDragGhost, p.X - _tabGrabDX);
            Canvas.SetTop(TabDragGhost, p.Y - 10);

            ShowDropCaret(over, e);
        }

        private void ShowDropCaret(FilePane target, MouseEventArgs e)
        {
            var strip = e.GetPosition(target.TabStrip);
            bool onStrip = target.TabBar.Visibility == Visibility.Visible
                           && strip.Y >= 0 && strip.Y <= target.TabBar.ActualHeight;

            if (!onStrip)
            {
                TabDropCaret.Visibility = Visibility.Collapsed;
                return;
            }

            int idx = InsertIndexFor(target, e);
            double w = target.Tabs.Count > 0 ? target.TabStrip.ActualWidth / target.Tabs.Count : 0;

            var at = target.TabStrip.TransformToVisual(DragLayer)
                                    .Transform(new Point(idx * w, 0));

            Canvas.SetLeft(TabDropCaret, at.X - 1);
            Canvas.SetTop(TabDropCaret, at.Y);
            TabDropCaret.Height = Math.Max(4, target.TabBar.ActualHeight);
            TabDropCaret.Visibility = Visibility.Visible;
        }

        private void HideDragFeedback()
        {
            if (!_ghostShown) return;
            _ghostShown = false;
            DragLayer.Visibility = Visibility.Collapsed;
            TabDropCaret.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Move <paramref name="t"/> into <paramref name="target"/>, at the position the drop
        /// implies, and leave it active and focused there.
        /// </summary>
        private void MoveTabToPane(SearchTab t, FilePane target, MouseEventArgs e)
        {
            var source = Pane;
            if (ReferenceEquals(source, target)) return;

            int insert = InsertIndexFor(target, e);

            // The control has to leave the old pane's slot BEFORE the tab does. A
            // TerminalControl can only have one visual parent, and leaving it parented to the
            // pane it is departing would throw the moment the new pane tried to show it.
            if (t.Term != null && ReferenceEquals(source.TerminalSlot.Content, t.Term))
                source.TerminalSlot.Content = null;

            bool wasActive = ReferenceEquals(source.Active, t);
            int wasAt = source.Tabs.IndexOf(t);

            source.Tabs.Remove(t);
            target.Tabs.Insert(Math.Min(insert, target.Tabs.Count), t);

            // Emptying EITHER pane collapses the window back to one. An empty pane is not a
            // thing you can look at, and the alternative for the left pane - handing it a fresh
            // blank tab - leaves you staring at a search form you never asked for while your
            // actual tabs sit in the other half.
            if (source.Tabs.Count == 0)
            {
                CollapseToSinglePane(t);
                return;
            }

            if (wasActive)
            {
                FocusPaneQuiet(source);
                // The neighbor that slid into the gap, the same choice closing a tab makes.
                ActivateTab(source.Tabs[Math.Min(wasAt, source.Tabs.Count - 1)]);
            }

            LandOn(target, t);
        }

        /// <summary>
        /// Fold the window back to one pane, leaving <paramref name="keep"/> active.
        /// </summary>
        /// <remarks>
        /// The layout has a primary side: LeftPane is the one that survives, and
        /// CloseSecondPane hides the right. So emptying the RIGHT pane is just a close, while
        /// emptying the LEFT one means the right pane's tabs have to come home first. Both end
        /// at the same place - one pane holding everything that was open.
        /// </remarks>
        private void CollapseToSinglePane(SearchTab keep)
        {
            if (LeftPane.Tabs.Count == 0)
            {
                // Detach before moving: a TerminalControl can only have one visual parent, and
                // the left pane is about to be asked to show it.
                RightPane.TerminalSlot.Content = null;

                foreach (var tab in RightPane.Tabs.ToList()) LeftPane.Tabs.Add(tab);
                RightPane.Tabs.Clear();
            }

            LeftPane.Active = keep;

            // Quiet, so CloseSecondPane's own FocusPane call sees no change and does not
            // activate a tab we are about to replace anyway.
            FocusPaneQuiet(LeftPane);
            CloseSecondPane();            // DualPane.cs

            ActivateTab(keep);            // Tabs.cs
            UpdateTabBar();
            CleanupTabTransforms();
        }

        /// <summary>Make the moved tab the active, focused one in its new home.</summary>
        private void LandOn(FilePane target, SearchTab t)
        {
            FocusPane(target);                // Panes.cs - the tab you just moved is the one
            ActivateTab(t);                   // you are looking at
            UpdateTabBar();                   // Tabs.cs - corner rounding and edge ownership
            CleanupTabTransforms();           // drop the drag offset the grabbed tab still has
        }

        /// <summary>
        /// Where in the target strip the drop belongs. Dropping on the tab bar inserts at the
        /// position under the pointer; dropping anywhere else in the pane appends, because
        /// there is no position being pointed at.
        /// </summary>
        private static int InsertIndexFor(FilePane target, MouseEventArgs e)
        {
            if (target.TabBar.Visibility != Visibility.Visible) return target.Tabs.Count;

            var p = e.GetPosition(target.TabStrip);
            if (p.Y < 0 || p.Y > target.TabBar.ActualHeight) return target.Tabs.Count;

            double w = target.Tabs.Count > 0 ? target.TabStrip.ActualWidth / target.Tabs.Count : 0;
            if (w <= 0) return target.Tabs.Count;

            // Rounded, so the boundary is the midpoint of a tab rather than its left edge -
            // dropping on the right half of a tab means "after this one".
            return Math.Max(0, Math.Min(target.Tabs.Count, (int)Math.Round(p.X / w)));
        }
    }
}
