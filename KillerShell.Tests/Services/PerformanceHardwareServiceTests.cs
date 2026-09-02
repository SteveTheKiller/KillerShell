using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class PerformanceHardwareServiceTests
    {
        [Fact]
        public void Gather_ReturnsCompleteCollectionShape()
        {
            PerformanceHardwareInfo info = PerformanceHardwareService.Gather();

            Assert.NotNull(info.Disks);
            Assert.NotNull(info.Gpus);
            Assert.NotNull(info.NetAdapters);
            Assert.False(string.IsNullOrEmpty(info.Cpu));
            Assert.False(string.IsNullOrEmpty(info.Ram));
            Assert.False(string.IsNullOrEmpty(info.Gpu));
            Assert.False(string.IsNullOrEmpty(info.Network));
        }
    }
}
