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

        [Fact]
        public void GpuLuid_ExtractsCounterInstanceIdentifier()
            => Assert.Equal("0x00000000_0x0000abcd",
                PerformanceMetricFormatter.GpuLuid("pid_4_luid_0x00000000_0x0000abcd_phys_0_eng_0_engtype_3D"));
    }
}
