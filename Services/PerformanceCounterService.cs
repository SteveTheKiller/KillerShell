using System;
using System.Diagnostics;

namespace KillerShell.Services
{
    internal static class PerformanceCounterService
    {
        internal static PerformanceCounter? Create(string category, string counter, string? instance = null,
            bool readOnly = true)
        {
            try
            {
                return instance == null
                    ? new PerformanceCounter(category, counter, readOnly)
                    : new PerformanceCounter(category, counter, instance, readOnly);
            }
            catch
            {
                return null;
            }
        }

        internal static bool TrySample(PerformanceCounter? counter, out double value)
        {
            value = 0;
            if (counter == null) return true;
            try
            {
                value = counter.NextValue();
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool Prime(PerformanceCounter? counter)
            => TrySample(counter, out _);

        internal static string[] GetInstanceNames(string category)
        {
            try
            {
                return new PerformanceCounterCategory(category).GetInstanceNames();
            }
            catch
            {
                return [];
            }
        }

        internal static double ClampPercent(double value)
            => Math.Min(100, Math.Max(0, value));

        internal static bool CategoryExists(string category)
        {
            try { return PerformanceCounterCategory.Exists(category); }
            catch { return false; }
        }
    }
}
