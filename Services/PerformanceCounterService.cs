using System;
using System.Collections.Generic;
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
            catch (Exception ex)
            {
                LastCreateError = ex.Message;
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

        /// <summary>
        /// Like TrySample, but a counter that never opened is no reading at all, for tiles
        /// whose whole display is that one counter.
        /// </summary>
        internal static bool TrySampleRequired(PerformanceCounter? counter, out double value)
        {
            value = 0;
            return counter != null && TrySample(counter, out value);
        }

        /// <summary>Why the most recent Create returned null, for the "counters unavailable" status.</summary>
        internal static string? LastCreateError { get; private set; }

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

        internal static Dictionary<string, double> AggregateGpuUtilization(IEnumerable<KeyValuePair<string, double>> samples)
        {
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var sample in samples)
            {
                int start = sample.Key.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                int end = sample.Key.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                if (start < 0 || end <= start || double.IsNaN(sample.Value) || double.IsInfinity(sample.Value)) continue;
                string engine = sample.Key[start..end];
                engines.TryGetValue(engine, out double current);
                engines[engine] = current + Math.Max(0, sample.Value);
            }
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var engine in engines)
            {
                string luid = PerformanceMetricFormatter.GpuLuid(engine.Key);
                result.TryGetValue(luid, out double current);
                result[luid] = Math.Max(current, ClampPercent(engine.Value));
            }
            return result;
        }

        internal static bool CategoryExists(string category)
        {
            try { return PerformanceCounterCategory.Exists(category); }
            catch { return false; }
        }
    }
}
