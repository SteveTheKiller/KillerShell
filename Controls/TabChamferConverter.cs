using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

// KillerShell, not KillerShell.Controls: every other file in this folder is in KillerShell, and
// all eleven XAML files here declare x:Class="KillerShell.<Name>", so the flat namespace IS the
// convention. This file was the only one out of step. IDE0130 argues the reverse - that the
// namespace should follow the folder - which here would mean rewriting every x:Class and every
// XAML namespace reference to satisfy an analyzer. Left flat deliberately.
namespace KillerShell
{
    /// <summary>
    /// Builds the clip geometry that gives a tab its Win98 chamfer: the two TOP corners cut at 45
    /// degrees instead of rounded. WPF's CornerRadius cannot express a chamfer at all, so the shape
    /// has to be real geometry.
    ///
    /// Bindings, in order: ActualWidth, ActualHeight, and the TabChamfer theme scalar.
    ///
    /// Returns null when the chamfer is 0 - which is every theme but 98SE - and a null Clip means
    /// the tab is not clipped at all, so the other twelve are untouched rather than being clipped
    /// to their own bounds for no reason.
    ///
    /// The geometry runs PAST the bottom edge (h + Overhang) on purpose. The tab's bevel borders
    /// carry a negative bottom margin (TabBevelMargin) so they reach the tab's real foot, and a
    /// clip that stopped at h would shear that overhang off and put back the very edge the
    /// TabBevelMargin work removed. Only the top corners are meant to be cut here.
    /// </summary>
    public sealed class TabChamferConverter : IMultiValueConverter
    {
        /// <summary>How far below the tab the clip extends, so the bevel overhang survives it.</summary>
        private const double Overhang = 16;

        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 3) return null;

            double w = values[0] as double? ?? 0;
            double h = values[1] as double? ?? 0;
            double c = values[2] as double? ?? 0;

            if (w <= 0 || h <= 0 || c <= 0) return null;

            // Never let the cut eat more than half the tab in either direction - a narrow tab with
            // a large chamfer would otherwise cross over itself and render as a triangle.
            c = Math.Min(c, Math.Min(w / 2, h / 2));

            double bottom = h + Overhang;

            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                // Start at the foot of the left edge and run up, cut across the top-left corner,
                // along the top, down the top-right cut, then back to the foot.
                ctx.BeginFigure(new Point(0, bottom), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(0, c), true, false);
                ctx.LineTo(new Point(c, 0), true, false);
                ctx.LineTo(new Point(w - c, 0), true, false);
                ctx.LineTo(new Point(w, c), true, false);
                ctx.LineTo(new Point(w, bottom), true, false);
            }

            g.Freeze();
            return g;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
