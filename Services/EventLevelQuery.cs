using System.Globalization;

namespace KillerShell.Services
{
    /// <summary>The XPath the events command sends to the log for one severity level.</summary>
    internal static class EventLevelQuery
    {
        // Event Viewer files level 0 (LogAlways) under Information, so Information takes both.
        internal static string For(int level)
            => level == 4
                ? "*[System[(Level=0 or Level=4)]]"
                : "*[System[(Level=" + level.ToString(CultureInfo.InvariantCulture) + ")]]";
    }
}
