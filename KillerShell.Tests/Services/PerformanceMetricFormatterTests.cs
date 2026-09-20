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
        public void Throughput_UsesBinaryUnits(double value, string expected)
            => Assert.Equal(expected, PerformanceMetricFormatter.Throughput(value));

        [Theory]
        [InlineData(0, 0, 0, 0, "0:00:00")]
        [InlineData(0, 4, 12, 55, "4:12:55")]
        [InlineData(3, 4, 12, 55, "3d 04:12:55")]
        [InlineData(120, 0, 0, 5, "120d 00:00:05")]
        public void UpTime_LeadsWithDaysOnceThereAreAny(int days, int hours, int minutes, int seconds, string expected)
            => Assert.Equal(expected, PerformanceMetricFormatter.UpTime(new System.TimeSpan(days, hours, minutes, seconds)));

        [Fact]
        public void GpuLuid_ExtractsCounterInstanceIdentifier()
            => Assert.Equal("0x00000000_0x0000abcd",
                PerformanceMetricFormatter.GpuLuid("pid_4_luid_0x00000000_0x0000abcd_phys_0_eng_0_engtype_3D"));
    }
}
