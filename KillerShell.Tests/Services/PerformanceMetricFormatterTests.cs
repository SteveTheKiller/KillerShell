using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class PerformanceMetricFormatterTests
    {
        [Theory]
        [InlineData(0UL, "")]
        [InlineData(100_000_000UL, "100 Mbps")]
        [InlineData(1_500_000_000UL, "1.5 Gbps")]
        public void LinkSpeed_UsesNetworkUnits(ulong value, string expected)
            => Assert.Equal(expected, PerformanceMetricFormatter.LinkSpeed(value));

        [Theory]
        [InlineData(512, "512 B/s")]
        [InlineData(1536, "1.5 KB/s")]
        [InlineData(1572864, "1.5 MB/s")]
        // A saturated 10 GbE link or several busy adapters summed: never wider than 999.9 MB/s,
        // the widest reading the summary box is sized for.
        [InlineData(1250000000, "1.2 GB/s")]
        public void Throughput_UsesBinaryUnits(double value, string expected)
            => Assert.Equal(expected, PerformanceMetricFormatter.Throughput(value));

        [Theory]
        [InlineData(0, 0, 0, 0, "0:00:00")]
        [InlineData(0, 4, 12, 55, "4:12:55")]
        [InlineData(3, 4, 12, 55, "3d 04:12:55")]
        [InlineData(120, 0, 0, 5, "120d 00:00:05")]
        public void UpTime_LeadsWithDaysOnceThereAreAny(int days, int hours, int minutes, int seconds, string expected)
            => Assert.Equal(expected, PerformanceMetricFormatter.UpTime(new System.TimeSpan(days, hours, minutes, seconds)));

        [Theory]
        [InlineData(26, 3200, "DDR4-3200")]
        [InlineData(34, 5600, "DDR5-5600")]
        [InlineData(24, 1600, "DDR3-1600")]
        [InlineData(30, 4267, "LPDDR4-4267")]
        [InlineData(35, 6400, "LPDDR5-6400")]
        [InlineData(26, 0, "DDR4")]
        [InlineData(0, 3200, "3200 MT/s")]
        [InlineData(2, 0, "")]   // a virtual machine's memory: SMBIOS "Unknown" and no speed
        public void MemoryKind_NamesTypeAndDataRate(int smbiosType, int speedMts, string expected)
            => Assert.Equal(expected, PerformanceMetricFormatter.MemoryKind([new MemoryModule(smbiosType, speedMts)]));

        [Fact]
        public void MemoryKind_ReadsTheSpeedTheSlowestStickRuns()
            => Assert.Equal("DDR4-2666", PerformanceMetricFormatter.MemoryKind(
                [new MemoryModule(26, 3200), new MemoryModule(26, 2666), new MemoryModule(0, 0)]));

        [Theory]
        [InlineData("DDR4-3200", "Memory", "DDR4-3200")]
        [InlineData("", "Memory", "Memory")]     // a virtual machine reports neither type nor speed
        [InlineData("-", "Speicher", "Speicher")] // nothing could be read at all
        public void PanelTitle_FallsBackToTheCardsName(string described, string label, string expected)
            => Assert.Equal(expected, PerformanceMetricFormatter.PanelTitle(described, label));

        [Theory]
        // SMBIOS before 2.8 gave the configured speed as a clock in MHz, half the data rate.
        [InlineData(800, 1600, 1600)]
        [InlineData(3200, 3200, 3200)]
        [InlineData(2666, 3200, 2666)]   // set to run slower than it is rated for
        [InlineData(0, 3200, 3200)]
        [InlineData(3200, 0, 3200)]
        public void DataRate_ReadsAnOldClockFieldAsTheRateItHalves(int configured, int rated, int expected)
            => Assert.Equal(expected, PerformanceMetricFormatter.DataRate(configured, rated));

        [Fact]
        public void MemoryKind_WithNoModulesIsEmpty()
            => Assert.Equal("", PerformanceMetricFormatter.MemoryKind([]));

        [Fact]
        public void GpuLuid_ExtractsCounterInstanceIdentifier()
            => Assert.Equal("0x00000000_0x0000abcd",
                PerformanceMetricFormatter.GpuLuid("pid_4_luid_0x00000000_0x0000abcd_phys_0_eng_0_engtype_3D"));
    }
}
