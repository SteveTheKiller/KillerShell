using System;
using System.Globalization;

namespace KillerShell.Services
{
    internal static class PerformanceMetricFormatter
    {
        internal static string LinkSpeed(ulong bitsPerSecond)
        {
            if (bitsPerSecond == 0) return string.Empty;
            double gbps = bitsPerSecond / 1_000_000_000.0;
            if (gbps >= 1.0) return gbps.ToString("0.#", CultureInfo.InvariantCulture) + " Gbps";
            return (bitsPerSecond / 1_000_000.0).ToString("0", CultureInfo.InvariantCulture) + " Mbps";
        }

        internal static string GpuLuid(string instanceName)
        {
            int index = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return instanceName;
            int start = index + 5;
            int end = instanceName.IndexOf("_phys", start, StringComparison.OrdinalIgnoreCase);
            return instanceName[start..(end < 0 ? instanceName.Length : end)];
        }

        internal static string Throughput(double bytesPerSecond)
        {
            const double kb = 1024;
            const double mb = kb * 1024;
            if (bytesPerSecond >= mb) return (bytesPerSecond / mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (bytesPerSecond >= kb) return (bytesPerSecond / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
            return bytesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " B/s";
        }

        internal static string Bytes(double bytes)
        {
            const double mb = 1024 * 1024;
            const double gb = mb * 1024;
            if (bytes >= gb) return (bytes / gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= mb) return (bytes / mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
            return bytes.ToString("0", CultureInfo.InvariantCulture) + " B";
        }

        /// <summary>"4:12:55", or "3d 04:12:55" once the machine has been up a day. The day count
        /// leads because it is the part that answers "when was this last restarted".</summary>
        internal static string UpTime(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            string clock = elapsed.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":"
                         + elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
            return elapsed.Days > 0
                ? elapsed.Days.ToString(CultureInfo.InvariantCulture) + "d "
                  + elapsed.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + clock
                : elapsed.Hours.ToString(CultureInfo.InvariantCulture) + ":" + clock;
        }
    }
}
