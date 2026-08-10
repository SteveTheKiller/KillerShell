using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace KillerShell.Shell
{
    // The open/close slide shared by both side panels. Partial of MainWindow.
    //
    // This was written inline for the search panel when that was the only collapsible thing in
    // the window. The folder tree needs exactly the same motion mirrored to the other edge, and
    // two copies of a hand-tuned animation drift apart, so it lives here once.
    //
    // The motion is KillerNotes': a 160ms width tween on the grid column, easing OUT on the way
    // open and IN on the way closed, so a panel accelerates away and settles gently on arrival.
    public partial class MainWindow
    {
        /// <summary>
        /// Tweens a grid column between 0 and <paramref name="width"/>, wiping the panel in or
        /// out from the window edge rather than reflowing its contents on every frame.
        /// </summary>
        /// <param name="freezeAlign">
        /// Which edge the panel's contents stay glued to while the column moves, and so which
        /// way the panel appears to travel. A column always gives up its width at its INNER
        /// edge, the one facing the content. Freeze on that inner edge and the panel travels
        /// with it, sliding OUT under the window's own frame. Freeze on the outer edge and the
        /// panel stands still while the content beside it wipes across the top of it, which
        /// reads as the window sliding IN over the panel.
        /// The tree is a left-hand panel and freezes Right, its inner edge, so it slides out to
        /// the left (TreePanel.cs). Read the note at each call site before changing one.
        /// </param>
        private void SlideColumn(ColumnDefinition col, FrameworkElement panel, bool open,
                                 double width, double minOpen, double maxOpen,
                                 HorizontalAlignment freezeAlign, bool animate)
        {
            // Reveal before the expand slide, or there is nothing to see sliding.
            if (open) panel.Visibility = Visibility.Visible;

            double target = open ? width : 0;

            if (!animate)
            {
                col.BeginAnimation(ColumnDefinition.WidthProperty, null);
                // MinWidth wins over Width in a Grid, so a column pinned at a MinWidth cannot be
                // collapsed by setting Width alone. Both move together.
                col.MinWidth = open ? minOpen : 0;
                col.Width    = new GridLength(target);
                if (!open) panel.Visibility = Visibility.Collapsed;
                return;
            }

            // Freeze the panel at its full width and pin it to its own edge so its contents do
            // NOT reflow while the column moves - the column edge wipes it in and out instead,
            // which is far cheaper than re-laying out a term list or a tree on every frame. On
            // expand the panel was only just re-shown, so its ActualWidth is still ~0 and the
            // target width stands in.
            double panelW = panel.ActualWidth > 8 ? panel.ActualWidth : width;
            panel.HorizontalAlignment = freezeAlign;
            panel.Width = panelW;

            // Min and Max are opened up for the tween and settled again when it lands, since a
            // MinWidth would otherwise clamp the animation partway.
            //
            // The start width is taken as-is, with no "> 0 ? ... : target" guard. KillerNotes can
            // afford that guard because its collapsed sidebar is still rail-width, so ActualWidth
            // is never 0 there. Here a collapsed column really is 0, so the guard fired on
            // exactly the open case and set From = To - an animation that travels nowhere, i.e.
            // the panel appearing instantly while closing still slid.
            double from = col.ActualWidth;
            col.MinWidth = 0;
            col.MaxWidth = double.PositiveInfinity;

            var anim = new GridLengthAnimation
            {
                From = from,
                To   = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new QuadraticEase
                    { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn },
            };

            anim.Completed += (_, _) =>
            {
                col.BeginAnimation(ColumnDefinition.WidthProperty, null);
                panel.ClearValue(FrameworkElement.WidthProperty);
                panel.HorizontalAlignment = HorizontalAlignment.Stretch;

                col.MinWidth = open ? minOpen : 0;
                col.MaxWidth = maxOpen;
                col.Width    = new GridLength(target);
                if (!open) panel.Visibility = Visibility.Collapsed;
            };

            col.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }
    }
}
