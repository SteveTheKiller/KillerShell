using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

// Responsive path rows and the existing file toolbar overflow. Partial of MainWindow.
// The same path/editor controls change grid cells; commands are never copied.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private static readonly string[] ShedOrder =
        [
            "ExportBtn", "FoldersTopBtn", "ShowHiddenBtn", "DetailsPaneBtn",
            "ExpandAllButton", "SortDirButton",
        ];

        // About 36 characters at the existing 12px monospace size, excluding the
        // localized prefix, padding and recents button. App scaling is applied by WPF.
        private const double MinFilenameWidth = 260;
        private const double PathRowHysteresis = 24;
        private readonly HashSet<FilePane> _reflowingToolbars = [];

        internal void ToolStrip_SizeChanged(FilePane pane) => ReflowToolStrip(pane);

        private static double MeasuredWidth(FrameworkElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return element.DesiredSize.Width;
        }

        private static bool NeedsSecondPathRow(double width, double required, bool wasCompact)
            => width < required + (wasCompact ? PathRowHysteresis : 0);

        private void ReflowToolStrip(FilePane pane)
        {
            if (pane?.ToolStrip == null || pane.LocationRow.ActualWidth <= 0
                || !_reflowingToolbars.Add(pane)) return;
            try
            {
                double nav = MeasuredWidth(pane.NavStrip);
                double margins = pane.ToolStrip.Margin.Left + pane.ToolStrip.Margin.Right;
                double fullTools = pane.ToolStrip.Children.OfType<FrameworkElement>()
                    .Where(el => el != pane.OverflowBtn).Concat(pane.OverflowPanel.Children.OfType<FrameworkElement>())
                    .Sum(MeasuredWidth);
                double path = MinFilenameWidth + MeasuredWidth(pane.ScopePrefixText)
                    + MeasuredWidth(pane.RecentsBtn) + pane.ScopeBar.Padding.Left + pane.ScopeBar.Padding.Right;
                bool wasCompact = Grid.GetRow(pane.ScopeBar) == 1;
                bool compact = NeedsSecondPathRow(pane.LocationRow.ActualWidth, nav + fullTools + margins + path, wasCompact);
                if (compact != wasCompact)
                {
                    // Close anchored flyouts before their buttons change rows.
                    pane.RecentsMenu.IsOpen = pane.SortPopup.IsOpen = pane.ExportPopup.IsOpen = pane.OverflowPopup.IsOpen = false;
                    Grid.SetRow(pane.ScopeBar, compact ? 1 : 0);
                    Grid.SetColumn(pane.ScopeBar, compact ? 0 : 1);
                    Grid.SetColumnSpan(pane.ScopeBar, compact ? 3 : 1);
                    pane.ScopeBar.Padding = compact ? new Thickness(6, 1, 8, 1) : new Thickness(6, 7, 8, 5);
                }

                // In compact mode the buttons have the entire first row. Compute from
                // the expanded button extent, never the strip's current ActualWidth.
                double avail = pane.LocationRow.ActualWidth - nav - margins - (compact ? 0 : path);
                double remaining = fullTools;
                double chevron = MeasuredWidth(pane.OverflowBtn);
                // Collapsed controls measure to zero, but the visible chevron is 20px.
                if (chevron == 0) chevron = pane.OverflowBtn.Width + pane.OverflowBtn.Margin.Left + pane.OverflowBtn.Margin.Right;
                var shed = new HashSet<string>();
                foreach (var name in ShedOrder)
                {
                    if (remaining + (shed.Count > 0 ? chevron : 0) <= avail) break;
                    if (Named(pane, name) is not FrameworkElement el || el.Visibility == Visibility.Collapsed) continue;
                    shed.Add(name);
                    remaining -= MeasuredWidth(el);
                }

                // Move only buttons whose membership changed. Reparenting every layout
                // pass would disturb keyboard focus and open flyout anchors.
                foreach (var name in ShedOrder.Reverse())
                {
                    if (Named(pane, name) is not FrameworkElement el) continue;
                    bool inOverflow = pane.OverflowPanel.Children.Contains(el);
                    if (shed.Contains(name) == inOverflow) continue;
                    pane.OverflowPopup.IsOpen = false;
                    if (inOverflow)
                    {
                        pane.OverflowPanel.Children.Remove(el);
                        InsertBack(pane, el, name);
                    }
                    else
                    {
                        pane.ToolStrip.Children.Remove(el);
                        pane.OverflowPanel.Children.Insert(0, el);
                    }
                }
                pane.OverflowBtn.Visibility = shed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _reflowingToolbars.Remove(pane); }
        }

        internal void TerminalBar_SizeChanged(FilePane pane)
        {
            if (pane?.TermBarLayout == null || pane.TermBarLayout.ActualWidth <= 0
                || !_reflowingToolbars.Add(pane)) return;
            try
            {
                double required = MeasuredWidth(pane.TermToolStrip) + MeasuredWidth(pane.TermPrefixText) + MinFilenameWidth;
                bool wasCompact = Grid.GetRow(pane.TermPathHost) == 1;
                bool compact = NeedsSecondPathRow(pane.TermBarLayout.ActualWidth, required, wasCompact);
                if (compact == wasCompact) return;
                Grid.SetRow(pane.TermPathHost, compact ? 1 : 0);
                Grid.SetColumnSpan(pane.TermPathHost, compact ? 2 : 1);
                Grid.SetColumn(pane.TermToolStrip, compact ? 0 : 1);
                Grid.SetColumnSpan(pane.TermToolStrip, compact ? 2 : 1);
                pane.TermToolStrip.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            }
            finally { _reflowingToolbars.Remove(pane); }
        }

        private static object? Named(FilePane pane, string name) => pane.FindName(name);

        private static void InsertBack(FilePane pane, FrameworkElement el, string name)
        {
            int want = System.Array.IndexOf(StripOrder, name);
            int at = pane.ToolStrip.Children.Count;
            for (int i = 0; i < pane.ToolStrip.Children.Count; i++)
            {
                if (pane.ToolStrip.Children[i] is not FrameworkElement c) continue;
                int idx = System.Array.IndexOf(StripOrder, c.Name);
                if (idx > want) { at = i; break; }
            }
            pane.ToolStrip.Children.Insert(at, el);
        }

        private static readonly string[] StripOrder =
        [
            "ViewListBtn", "ViewIconsBtn", "ViewDetailsBtn", "SortBtn", "SortDirButton",
            "ExpandAllButton", "ShowHiddenBtn", "FoldersTopBtn", "DetailsPaneBtn",
            "PipeBtn", "ExportBtn", "OverflowBtn",
        ];

        internal static void Overflow_Click(FilePane pane)
        {
            var p = pane.OverflowPopup;
            p.IsOpen = !p.IsOpen;
            if (p.IsOpen && p.Child is UIElement child) Anim.FadeIn(child);
        }
    }
}
