using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KillerShell.Services
{
    /// <summary>One installed memory stick as Win32_PhysicalMemory reports it: its SMBIOS memory
    /// type code and the data rate it runs at, in MT/s (0 when the firmware does not say).</summary>
    internal readonly record struct MemoryModule(int SmbiosType, int SpeedMts);

    internal static class PerformanceMetricFormatter
    {
        /// <summary>"DDR4-3200": the memory generation and the data rate the installed sticks run
        /// at together, which is the slowest of them. Either half alone when the firmware reports
        /// only that half, and empty when it reports neither (a virtual machine's memory).</summary>
        internal static string MemoryKind(IReadOnlyList<MemoryModule> modules)
        {
            string type = modules
                .Select(module => MemoryTypeName(module.SmbiosType))
                .Where(name => name.Length > 0)
                .GroupBy(name => name, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault() ?? string.Empty;
            int speed = modules.Where(module => module.SpeedMts > 0).Select(module => module.SpeedMts).DefaultIfEmpty(0).Min();
            string rate = speed.ToString(CultureInfo.InvariantCulture);

            if (type.Length > 0) return speed > 0 ? type + "-" + rate : type;
            return speed > 0 ? rate + " MT/s" : string.Empty;
        }

        /// <summary>The data rate a stick runs at, in MT/s, from Win32_PhysicalMemory's configured and
        /// rated speeds. SMBIOS before 2.8 gave the configured speed as a clock in MHz, half the
        /// data rate, so a configured speed that is exactly half the rated one is that old clock.</summary>
        internal static int DataRate(int configured, int rated)
        {
            if (configured <= 0) return Math.Max(rated, 0);
            if (rated > 0 && Math.Abs(configured * 2 - rated) <= 2) return rated;
            return configured;
        }

        /// <summary>A panel's title: what the hardware is, or the card's own name when the machine
        /// does not say (a virtual machine's memory has no type or speed).</summary>
        internal static string PanelTitle(string described, string label)
            => string.IsNullOrWhiteSpace(described) || described == "-" ? label : described;

        // SMBIOS memory device types (DMTF DSP0134, 7.18.2) for the generations still in use.
        private static string MemoryTypeName(int smbiosType) => smbiosType switch
        {
            0x12 => "DDR",
            0x13 or 0x14 => "DDR2",
            0x18 => "DDR3",
            0x1A => "DDR4",
            0x1B => "LPDDR",
            0x1C => "LPDDR2",
            0x1D => "LPDDR3",
            0x1E => "LPDDR4",
            0x22 => "DDR5",
            0x23 => "LPDDR5",
            _ => string.Empty,
        };

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
            const double gb = mb * 1024;
            // From 1000 MB/s on, so no reading is wider than "999.9 MB/s".
            if (bytesPerSecond >= 1000 * mb) return (bytesPerSecond / gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB/s";
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
