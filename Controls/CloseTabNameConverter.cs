using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace KillerShell
{
    // Each tab's close button is named after its tab ("Close tab (Ctrl+W), Downloads"), so a
    // screen reader can tell one from another. The first value is the button's own localized
    // tooltip, which keeps the name current when the language changes; the second is the title.
    public sealed class CloseTabNameConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string close = values.Length > 0 && values[0] is string text ? Regex.Replace(text.Trim(), @"\s+", " ") : "";
            string title = values.Length > 1 && values[1] is string name ? name.Trim() : "";
            if (title.Length == 0) return close;
            return close.Length == 0 ? title : close + ", " + title;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
