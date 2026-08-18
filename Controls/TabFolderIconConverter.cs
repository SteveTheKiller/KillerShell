using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;

namespace KillerShell
{
    // The tab strip's icon, for EVERY kind of tab - a folder tab's own folder icon, and the brand
    // art for a shell, an editor, Processes, Event Viewer, Performance, Registry Editor or
    // Storage Analyzer.
    //
    // It started as folder-tabs-only (a browsing tab used to carry no icon at all, because
    // TabGlyph - the strip's other slot - was a Segoe MDL2 glyph reserved for the non-folder
    // kinds). Now that there is real art for every kind, this answers for all of them and the
    // glyph slot is only a fallback for anything without a picture (2026-08-08).
    //
    // Bound to the WHOLE tab, not a single property, because the answer depends on several at
    // once - a search tab has a CurrentFolder too (its last-clicked result's folder) but is not
    // a folder tab and must not grow a folder icon.
    public sealed class TabFolderIconConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not SearchTab t) return null;

            // Ordered by how specific the test is. IsBrowsing is LAST of the real kinds because a
            // tab can be browsing and something else at the same time.
            if (t.IsTerminal)
            {
                // Read off the GLYPH the strip already carries rather than the terminal's own
                // state: TerminalProfile hands the tab E756 for a shell and E7EF for an elevated
                // one, and TerminalTabs.cs swaps in E711 when the shell exits. TerminalControl
                // does not expose its profile, and duplicating the decision here would let the
                // icon and the glyph disagree about the same tab.
                return IconCache.Art(Glyph(t) switch
                {
                    (char)0xE711 => "dead_shell_icon",
                    (char)0xE7EF => "admin_term_icon",
                    _            => "term_icon",
                });
            }
            if (t.IsEditor)             return IconCache.Art("text_editor_icon");
            if (t.IsProcessList)        return IconCache.Art("task_manager");
            if (t.IsEventViewer)        return IconCache.Art("event_viewer");
            if (t.IsPerformanceMonitor) return IconCache.Art("perf_icon");
            if (t.IsRegistryEditor)     return IconCache.Art("registry_editor_icon");
            // The analyzer represents disk usage, so use the exact theme-aware hard-drive art
            // the folder tree uses for drive roots instead of its old generic MDL2 dash.
            if (t.IsStorageAnalyzer)    return IconCache.Art("drive_icon");

            // A SEARCH tab. Tested on IsSearching, NOT on !IsBrowsing: a fresh or Home tab is
            // also not browsing, and keying off that would have stamped the search icon on every
            // empty tab. A search tab carries a CurrentFolder too - its last-clicked result's
            // folder - which is why this has to come before the folder branch.
            if (t.IsSearching) return IconCache.Art("search_results_icon");

            if (!t.IsBrowsing || string.IsNullOrEmpty(t.CurrentFolder)) return null;
            if (!Directory.Exists(t.CurrentFolder)) return null;

            // Matches the Image's draw size (FilePane.xaml tabFolderIcon). Note the px argument
            // only selects the shell image LIST, and ShilFor buckets everything up to 32 the same
            // way - so between 14 and 32 this changes the cache key and nothing else. The brand
            // art ignores it entirely and is scaled by the Image, which is why the fix for a
            // crushed tab icon is the Width/Height there, not this number.
            return IconCache.For(t.CurrentFolder, 20, isDirectory: true);
        }

        private static char Glyph(SearchTab t) =>
            string.IsNullOrEmpty(t.TabGlyph) ? '\0' : t.TabGlyph[0];

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// True when <see cref="TabFolderIconConverter"/> has art for this tab. The tab template hides
    /// the Segoe MDL2 glyph when it does, so a shell or Processes tab shows its picture instead of
    /// both. One converter asking the other keeps the two decisions from drifting apart.
    /// </summary>
    public sealed class TabHasArtConverter : IValueConverter
    {
        private static readonly TabFolderIconConverter Icon = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => Icon.Convert(value, typeof(ImageSource), parameter!, culture) != null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
