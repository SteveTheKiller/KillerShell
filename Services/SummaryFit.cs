using System;

namespace KillerShell.Services
{
    /// <summary>
    /// How a Performance summary box's top line gives way when the box gets narrow: the icon
    /// beside the reading shrinks first, then goes, and only then does the number itself get
    /// smaller. The number is what the box is for, so it keeps its full size the longest and is
    /// never cut off while there is any size left to give.
    /// </summary>
    internal static class SummaryFit
    {
        internal const double IconLargest = 28;
        internal const double IconSmallest = 16;
        internal const double IconGap = 8;
        internal const double ValueLargest = 20;
        internal const double ValueSmallest = 11;

        /// <summary>The icon size (0 for none) and the number's font size for a box
        /// <paramref name="available"/> wide whose widest reading is
        /// <paramref name="readingWidth"/> wide at full size. Whole pixels and points, so
        /// neither sits between two and blurs.</summary>
        internal static (double Icon, double Value) Choose(double available, double readingWidth)
        {
            double icon = Math.Floor(Math.Min(IconLargest, available - readingWidth - IconGap));
            if (icon >= IconSmallest) return (icon, ValueLargest);
            if (readingWidth <= available || readingWidth <= 0) return (0, ValueLargest);
            return (0, Math.Max(ValueSmallest, Math.Floor(ValueLargest * available / readingWidth)));
        }
    }
}
