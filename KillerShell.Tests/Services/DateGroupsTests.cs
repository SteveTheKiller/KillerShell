using System;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class DateGroupsTests
    {
        // Sunday 2026-09-20, week starting Sunday: the listing the feature was modeled on.
        private static readonly DateTime Today = new(2026, 9, 20);

        [Theory]
        [InlineData("2026-09-20 08:00", DateGroups.Today)]
        [InlineData("2026-09-21 08:00", DateGroups.Today)]       // clock skew reads as today
        [InlineData("2026-09-19 23:59", DateGroups.Yesterday)]
        [InlineData("2026-09-14 22:09", DateGroups.LastWeek)]
        [InlineData("2026-09-13 00:00", DateGroups.LastWeek)]
        [InlineData("2026-09-12 12:00", DateGroups.ThisMonth)]
        [InlineData("2026-09-01 00:00", DateGroups.ThisMonth)]
        [InlineData("2026-08-23 17:08", DateGroups.LastMonth)]
        [InlineData("2026-08-01 00:00", DateGroups.LastMonth)]
        [InlineData("2026-07-19 17:39", DateGroups.ThisYear)]
        [InlineData("2025-12-31 23:59", DateGroups.LongAgo)]
        public void KeyFor_SundayWeek(string value, int expected)
            => Assert.Equal(expected, DateGroups.KeyFor(DateTime.Parse(value), Today, DayOfWeek.Sunday));

        [Theory]
        [InlineData("2026-09-16 09:00", DateGroups.ThisWeek)]    // Wednesday, week began Monday the 14th
        [InlineData("2026-09-14 09:00", DateGroups.ThisWeek)]
        [InlineData("2026-09-13 09:00", DateGroups.LastWeek)]
        [InlineData("2026-09-07 09:00", DateGroups.LastWeek)]
        [InlineData("2026-09-06 09:00", DateGroups.ThisMonth)]
        public void KeyFor_MondayWeek(string value, int expected)
            => Assert.Equal(expected, DateGroups.KeyFor(DateTime.Parse(value), new DateTime(2026, 9, 18), DayOfWeek.Monday));

        [Fact]
        public void KeyFor_NoDate_IsLongAgo()
            => Assert.Equal(DateGroups.LongAgo, DateGroups.KeyFor(default, Today, DayOfWeek.Sunday));

        [Fact]
        public void KeyFor_WeekStraddlingMonthStart_PrefersTheWeek()
            => Assert.Equal(DateGroups.ThisWeek,
                   DateGroups.KeyFor(new DateTime(2026, 8, 31), new DateTime(2026, 9, 3), DayOfWeek.Monday));
    }
}
