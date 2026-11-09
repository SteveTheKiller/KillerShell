using System;

namespace KillerShell.Services
{
    /// <summary>
    /// The ISO date and time KillerShell shows on screen, the same shape the exports write.
    /// </summary>
    internal static class DisplayTime
    {
        // InvariantCulture, because the current one decides the calendar and the time separator:
        // Thai settings turn 2026 into 2569 and Finnish ones turn 14:30 into 14.30.
        internal static string Minutes(DateTime value)
            => value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        internal static string Seconds(DateTime value)
            => value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
