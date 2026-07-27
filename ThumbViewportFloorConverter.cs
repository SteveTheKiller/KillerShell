using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KillerShell
{
    // Keeps the scrollbar thumb sized to the visible proportion while never shrinking below a
    // grabbable floor. (KillerUI / Grunge)
    // Bindings (in order): ViewportSize, Maximum, Minimum, ActualWidth, ActualHeight, Orientation.
    // ConverterParameter: the floor in pixels (default 64).
    public sealed class ThumbViewportFloorConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double vp = AsDouble(values, 0);
            if (double.IsNaN(vp) || vp <= 0) return vp;

            double max     = AsDouble(values, 1);
            double min     = AsDouble(values, 2);
            double width   = AsDouble(values, 3);
            double height  = AsDouble(values, 4);
            bool vertical  = values.Length <= 5 || values[5] is not Orientation o
                                 || o == Orientation.Vertical;

            double trackLen = vertical ? height : width;
            double range    = max - min;
            if (double.IsNaN(trackLen) || trackLen <= 0 || range <= 0) return vp;

            double floor = 64;
            if (parameter != null &&
                double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p > 0)
                floor = p;

            floor = Math.Min(floor, trackLen * 0.5);
            if (trackLen <= floor) return vp;

            double vpForFloor = floor * range / (trackLen - floor);
            return Math.Max(vp, vpForFloor);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static double AsDouble(object[] values, int i)
        {
            if (values == null || i >= values.Length) return double.NaN;
            object v = values[i];
            if (v == null || v == DependencyProperty.UnsetValue) return double.NaN;
            try { return System.Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return double.NaN; }
        }
    }
}
