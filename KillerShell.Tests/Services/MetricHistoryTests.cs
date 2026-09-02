using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class MetricHistoryTests
    {
        [Fact]
        public void Push_KeepsOnlyCapacityAndAlignsSeries()
        {
            var history = new MetricHistory(2, 3, 100);
            history.Push(1, 10);
            history.Push(2, 20);
            history.Push(3, 30);
            history.Push(4, 40);

            Assert.Equal(new double[] { 2, 3, 4 }, history.Series[0]);
            Assert.Equal(new double[] { 20, 30, 40 }, history.Series[1]);
            Assert.Equal(100, history.ScaleMax);
        }

        [Fact]
        public void Push_AutoScaleTracksLargestRetainedSample()
        {
            var history = new MetricHistory(1, 2, 0);
            history.Push(10);
            Assert.Equal(12, history.ScaleMax);
            history.Push(20);
            history.Push(5);
            Assert.Equal(24, history.ScaleMax);
            history.Push(4);
            Assert.Equal(6, history.ScaleMax);
        }
    }
}
