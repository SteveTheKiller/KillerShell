using KillerShell.Services;
using System.Collections.Generic;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class PerformanceCounterServiceTests
    {
        [Theory]
        [InlineData(-5, 0)]
        [InlineData(42.5, 42.5)]
        [InlineData(125, 100)]
        public void ClampPercent_StaysInGraphRange(double value, double expected)
            => Assert.Equal(expected, PerformanceCounterService.ClampPercent(value));

        [Fact]
        public void TrySample_NullCounterRepresentsUnavailableZero()
        {
            Assert.True(PerformanceCounterService.TrySample(null, out double value));
            Assert.Equal(0, value);
        }

        // The disk and network tiles have no reading without their counters. A counter that
        // could not be opened is not a sample of zero, so those tiles show a dash.
        [Fact]
        public void TrySampleRequired_AMissingCounterIsNotASample()
            => Assert.False(PerformanceCounterService.TrySampleRequired(null, out _));

        // When no counter opens at all, the tab says why instead of showing zeros.
        [Fact]
        public void Create_AFailedCounterKeepsItsReason()
        {
            Assert.Null(PerformanceCounterService.Create("KillerShell No Such Category " + System.Guid.NewGuid().ToString("N"), "Nothing"));
            Assert.False(string.IsNullOrWhiteSpace(PerformanceCounterService.LastCreateError));
        }

        [Fact]
        public void GpuUtilization_SumsProcessesOnEachEngineAndUsesTheBusiestEngine()
        {
            var samples = new Dictionary<string, double>
            {
                ["pid_1_luid_0x00000000_0x0000000a_phys_0_eng_0_engtype_3D"] = 30,
                ["pid_2_luid_0x00000000_0x0000000a_phys_0_eng_0_engtype_3D"] = 15,
                ["pid_1_luid_0x00000000_0x0000000a_phys_0_eng_1_engtype_VideoDecode"] = 50,
                ["pid_2_luid_0x00000000_0x0000000a_phys_0_eng_1_engtype_VideoDecode"] = 25,
                ["pid_3_luid_0x00000000_0x0000000b_phys_0_eng_1_engtype_Compute"] = 20,
            };
            var actual = PerformanceCounterService.AggregateGpuUtilization(samples);
            Assert.Equal(75, actual["0x00000000_0x0000000a"]);
            Assert.Equal(20, actual["0x00000000_0x0000000b"]);
        }
    }
}
