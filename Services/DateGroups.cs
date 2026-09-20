using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KillerShell.Services
{
    // Explorer's date buckets for a listing sorted by a date column: Today, Yesterday, Earlier
    // this week, Last week, Earlier this month, Last month, Earlier this year, A long time ago.
    //
    // The key is an int rather than the heading text so the grouping itself never depends on
    // the interface language; the heading is looked up when it is drawn (DateGroupNameConverter).
    // Keys ascend with age, so a newest-first sort lays the groups out in reading order.
    public static class DateGroups
    {
        public const int Today = 0, Yesterday = 1, ThisWeek = 2, LastWeek = 3,
                         ThisMonth = 4, LastMonth = 5, ThisYear = 6, LongAgo = 7;

        public static int KeyFor(DateTime value) => KeyFor(value, DateTime.Today,
            CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);

        // Each boundary is tested from the newest inward, so a date lands in the FIRST bucket
        // that holds it: the 1st of the month on a Tuesday is "earlier this week", not "earlier
        // this month".
        internal static int KeyFor(DateTime value, DateTime today, DayOfWeek firstDayOfWeek)
        {
            if (value == default) return LongAgo;

            DateTime d = value.Date;
            if (d >= today) return Today;                       // a clock-skewed future date too
            if (d == today.AddDays(-1)) return Yesterday;

            int intoWeek = ((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
            DateTime weekStart = today.AddDays(-intoWeek);
            if (d >= weekStart) return ThisWeek;
            if (d >= weekStart.AddDays(-7)) return LastWeek;

            var monthStart = new DateTime(today.Year, today.Month, 1);
            if (d >= monthStart) return ThisMonth;
            if (d >= monthStart.AddMonths(-1)) return LastMonth;

            return d.Year == today.Year ? ThisYear : LongAgo;
        }

        public static string ResourceKey(int key) => key switch
        {
            Today     => "Str_DateGroup_Today",
            Yesterday => "Str_DateGroup_Yesterday",
            ThisWeek  => "Str_DateGroup_ThisWeek",
            LastWeek  => "Str_DateGroup_LastWeek",
            ThisMonth => "Str_DateGroup_ThisMonth",
            LastMonth => "Str_DateGroup_LastMonth",
            ThisYear  => "Str_DateGroup_ThisYear",
            _         => "Str_DateGroup_LongAgo",
        };
    }

    /// <summary>A group's int key to its heading in the current interface language.</summary>
    public sealed class DateGroupNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int key
                ? Application.Current?.TryFindResource(DateGroups.ResourceKey(key)) as string ?? string.Empty
                : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
