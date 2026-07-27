using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

// The pane toolbar's overflow. Partial of MainWindow.
//
// The location row is nav buttons, the folder path, then a strip of eight icon buttons. The path
// column is the star, so it gives up its width first and then the button strip simply ran off the
// edge of a narrow pane - which is what dual pane made obvious, because half a window is narrow.
//
// Buttons are MOVED between the strip and the overflow popup, never duplicated. A copy would need
// its own handler and its own lit/unlit state, and the two would drift the first time one of them
// changed; moving keeps exactly one button that happens to be parented somewhere else.
//
// Takes the pane as an argument rather than going through `Pane`: this is driven by a layout
// event on a specific pane, which is not necessarily the focused one - the unfocused pane resizes
// too, and reflowing the wrong pane's strip would be worse than not reflowing at all.
namespace KillerShell
{
    public partial class MainWindow
    {
        // Shed order, least useful first. Sort and the three view toggles are deliberately absent:
        // they are the controls you reach for constantly, and the strip is not worth having if
        // those are the ones that vanish. The chevron itself is excluded too - it must never be
        // able to push itself out.
        private static readonly string[] ShedOrder =
        {
            "ExportBtn", "FoldersTopBtn", "ShowHiddenBtn",
            "ExpandAllButton", "SortDirButton",
        };

        /// <summary>
        /// Width the path is never squeezed below. The strip may take everything else the row
        /// has; only what is left over after this counts as an overrun.
        /// </summary>
        private const double MinPathWidth = 150;

        internal void ToolStrip_SizeChanged(FilePane pane) => ReflowToolStrip(pane);

        /// <summary>
        /// Move buttons between the strip and the overflow popup until the strip fits.
        ///
        /// The budget is measured from the ROW, never from the strip's own ActualWidth. The strip
        /// sits in an Auto column, so shedding a button shrinks its ActualWidth - measuring
        /// against that fed back on itself: shed once, next layout pass sees a narrower strip,
        /// sheds again, and the row emptied into the overflow menu on a window with plenty of
        /// room. The re-entrancy guard does not catch it because the follow-up SizeChanged is a
        /// separate layout pass, not recursion.
        ///
        /// Row width minus the nav buttons minus a floor for the path is stable: it does not
        /// move when a button is shed, so the decision converges instead of cascading.
        /// </summary>
        private bool _reflowing;

        private void ReflowToolStrip(FilePane pane)
        {
            if (_reflowing || pane?.ToolStrip == null || pane.LocationRow == null) return;
            _reflowing = true;
            try
            {
                double avail = pane.LocationRow.ActualWidth
                             - pane.NavStrip.ActualWidth
                             - MinPathWidth
                             - pane.ToolStrip.Margin.Left - pane.ToolStrip.Margin.Right;
                if (avail <= 0) return;

                // Everything back in the strip first, in the declared order, so the decision is
                // made from a known state rather than from wherever the last pass left things.
                foreach (var name in ShedOrder.Reverse())
                {
                    if (Named(pane, name) is not FrameworkElement el) continue;
                    if (pane.OverflowPanel.Children.Contains(el))
                    {
                        pane.OverflowPanel.Children.Remove(el);
                        InsertBack(pane, el, name);
                    }
                }
                pane.OverflowBtn.Visibility = Visibility.Collapsed;

                // Shed until it fits. Measuring at infinity gives what the strip WANTS; the
                // chevron's own width has to be paid for as soon as anything is shed.
                for (int i = 0; i < ShedOrder.Length && Wanted(pane) > avail; i++)
                {
                    if (Named(pane, ShedOrder[i]) is not FrameworkElement el) continue;
                    if (el.Visibility == Visibility.Collapsed) continue;   // already hidden by a view mode

                    pane.ToolStrip.Children.Remove(el);
                    pane.OverflowPanel.Children.Insert(0, el);
                    pane.OverflowBtn.Visibility = Visibility.Visible;
                }
            }
            finally { _reflowing = false; }
        }

        private static double Wanted(FilePane pane)
        {
            pane.ToolStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return pane.ToolStrip.DesiredSize.Width;
        }

        private static object? Named(FilePane pane, string name) => pane.FindName(name);

        /// <summary>
        /// Put a button back where it belongs. The strip's declared order is the source of truth,
        /// so a button returning from overflow lands next to the same neighbors it left - it
        /// cannot end up shuffled to the end of the row after a few resizes.
        /// </summary>
        private static void InsertBack(FilePane pane, FrameworkElement el, string name)
        {
            var order = StripOrder;
            int want = System.Array.IndexOf(order, name);
            int at = pane.ToolStrip.Children.Count;

            for (int i = 0; i < pane.ToolStrip.Children.Count; i++)
            {
                if (pane.ToolStrip.Children[i] is not FrameworkElement c || c.Name == null) continue;
                int idx = System.Array.IndexOf(order, c.Name);
                if (idx > want) { at = i; break; }
            }
            pane.ToolStrip.Children.Insert(at, el);
        }

        // Declared left-to-right order of everything in the strip, used to restore position.
        private static readonly string[] StripOrder =
        {
            "ViewListBtn", "ViewIconsBtn", "ViewDetailsBtn", "SortBtn", "SortDirButton",
            "ExpandAllButton", "ShowHiddenBtn", "FoldersTopBtn",
            "PipeBtn", "ExportBtn", "OverflowBtn",
        };

        internal void Overflow_Click(FilePane pane)
        {
            var p = pane.OverflowPopup;
            p.IsOpen = !p.IsOpen;
            if (p.IsOpen && p.Child is UIElement child) Anim.FadeIn(child);   // Anim.cs
        }
    }
}
