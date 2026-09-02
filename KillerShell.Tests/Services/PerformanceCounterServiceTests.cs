using KillerShell.Services;
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
    }
}
