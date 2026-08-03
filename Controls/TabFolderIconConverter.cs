using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;

namespace KillerShell
{
    // The real Windows shell icon for a folder tab's OWN folder - the same source the tab-strip
    // overflow dropdown already shows successfully (Tabs.cs OverflowRowIcon), now in the strip
    // itself: a browsing tab used to carry no icon at all (TabGlyph, the strip's only icon slot,
    // is reserved for shell/document/Processes tabs and is empty for a folder tab by design).
    //
    // Bound to the WHOLE tab (SearchTab.CurrentFolder/IsBrowsing are both notifying as of
    // 2026-08-02 for exactly this), not a single property, because the answer depends on both
    // together - a search tab has a CurrentFolder too (its last-clicked result's folder) but is
    // not one, and should not suddenly grow a folder icon.
    public sealed class TabFolderIconConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not SearchTab t) return null;
            if (!t.IsBrowsing || string.IsNullOrEmpty(t.CurrentFolder)) return null;
            if (!Directory.Exists(t.CurrentFolder)) return null;
            return IconCache.For(t.CurrentFolder, 14, isDirectory: true);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
