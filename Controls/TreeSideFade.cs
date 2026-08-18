using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KillerShell.Controls
{
    /// <summary>
    /// Dissolves horizontally clipped tree content just before the viewport edge. The vertical
    /// scrollbar is deliberately restored to full opacity, so the fade reads as a cue on the
    /// rows rather than as a shadow painted over the control chrome.
    /// </summary>
    internal static class TreeSideFade
    {
        private static readonly DependencyProperty IsAttachedProperty =
            DependencyProperty.RegisterAttached("IsAttached", typeof(bool), typeof(TreeSideFade));

        internal static void Attach(TreeView tree)
        {
            if ((bool)tree.GetValue(IsAttachedProperty)) return;
            tree.SetValue(IsAttachedProperty, true);

            var state = new State(tree);
            tree.Loaded += (_, _) => state.Sync();
            tree.SizeChanged += (_, _) => state.Sync();
            tree.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => state.Sync()), handledEventsToo: true);
        }

        private sealed class State
        {
            private const double FadeWidth = 24;
            private readonly TreeView _tree;
            private readonly LinearGradientBrush _mask;

            internal State(TreeView tree)
            {
                _tree = tree;
                _mask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0),
                    MappingMode = BrushMappingMode.RelativeToBoundingBox
                };
                _tree.OpacityMask = _mask;
            }

            internal void Sync()
            {
                ScrollViewer? scroller = FindDescendant<ScrollViewer>(_tree);
                double width = _tree.ActualWidth;
                if (scroller == null || width <= 1) return;

                double themeFade = Application.Current.TryFindResource("EdgeFadeOpacity") is double e
                    ? e : 1.0;
                double left = Ramp(scroller.HorizontalOffset, FadeWidth) * themeFade;
                double right = Ramp(
                    scroller.ExtentWidth - scroller.ViewportWidth - scroller.HorizontalOffset,
                    FadeWidth) * themeFade;

                double barWidth = 0;
                if (scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                    barWidth = FindVerticalBar(scroller)?.ActualWidth
                        ?? SystemParameters.VerticalScrollBarWidth;

                double leftInner = Offset(FadeWidth, width);
                double rightOuter = Offset(Math.Max(0, width - barWidth), width);
                double rightInner = Offset(Math.Max(0, width - barWidth - FadeWidth), width);
                double restore = Math.Min(1, rightOuter + 0.001);

                _mask.GradientStops.Clear();
                _mask.GradientStops.Add(new GradientStop(Alpha(1 - left), 0));
                _mask.GradientStops.Add(new GradientStop(Alpha(1 - left * 0.35), leftInner * 0.45));
                _mask.GradientStops.Add(new GradientStop(Colors.Black, leftInner));
                _mask.GradientStops.Add(new GradientStop(Colors.Black, rightInner));
                _mask.GradientStops.Add(new GradientStop(Alpha(1 - right * 0.35),
                    rightInner + (rightOuter - rightInner) * 0.55));
                _mask.GradientStops.Add(new GradientStop(Alpha(1 - right), rightOuter));
                if (barWidth > 0.5)
                    _mask.GradientStops.Add(new GradientStop(Colors.Black, restore));
                _mask.GradientStops.Add(new GradientStop(Colors.Black, 1));
            }

            private static double Offset(double x, double width) =>
                Math.Min(1, Math.Max(0, x / width));

            private static double Ramp(double distance, double width) =>
                Math.Min(1, Math.Max(0, distance) / width);
        }

        private static Color Alpha(double a) =>
            Color.FromArgb((byte)Math.Round(Math.Min(1, Math.Max(0, a)) * 255), 0, 0, 0);

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                T? nested = FindDescendant<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private static ScrollBar? FindVerticalBar(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollBar bar && bar.Orientation == Orientation.Vertical)
                    return bar;
                ScrollBar? nested = FindVerticalBar(child);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
