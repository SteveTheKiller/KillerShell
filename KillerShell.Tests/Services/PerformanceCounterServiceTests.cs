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
