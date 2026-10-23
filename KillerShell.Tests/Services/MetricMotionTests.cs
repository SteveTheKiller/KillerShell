using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class MetricMotionTests
    {
        [Fact]
        public void Progress_RunsFromTheSampleToOnePeriodLater()
        {
            Assert.Equal(0, MetricMotion.Progress(now: 9.5, startedAt: 10, duration: 1));
            Assert.Equal(0, MetricMotion.Progress(now: 10, startedAt: 10, duration: 1));
            Assert.Equal(0.25, MetricMotion.Progress(now: 10.25, startedAt: 10, duration: 1));
            Assert.Equal(1, MetricMotion.Progress(now: 11, startedAt: 10, duration: 1));
            Assert.Equal(1, MetricMotion.Progress(now: 40, startedAt: 10, duration: 1));
            Assert.Equal(1, MetricMotion.Progress(now: 10, startedAt: 10, duration: 0));
        }

        [Fact]
        public void BlendedValue_FirstSampleLandsAsIs()
        {
            var value = new BlendedValue();
            value.Retarget(40, now: 5, duration: 1);
            Assert.Equal(40, value.At(5));
            Assert.Equal(40, value.At(5.5));
        }

        [Fact]
        public void BlendedValue_CountsFromWhatIsShownToTheNewSample()
        {
            var value = new BlendedValue();
            value.Retarget(0, now: 0, duration: 1);
            value.Retarget(100, now: 1, duration: 1);

            Assert.Equal(0, value.At(1));
            Assert.Equal(25, value.At(1.25));
            Assert.Equal(75, value.At(1.75));
            Assert.Equal(100, value.At(2));
            Assert.Equal(100, value.At(9));
        }

        [Fact]
        public void BlendedValue_ASampleThatArrivesMidCountStartsFromTheShownValue()
        {
            var value = new BlendedValue();
            value.Retarget(0, now: 0, duration: 1);
            value.Retarget(100, now: 1, duration: 1);
            Assert.Equal(50, value.At(1.5));

            value.Retarget(0, now: 1.5, duration: 1);
            Assert.Equal(50, value.At(1.5));
            Assert.Equal(25, value.At(2));
            Assert.Equal(0, value.At(2.5));
        }

        [Fact]
        public void BlendedValue_SettlesOnceItsDurationHasPassed()
        {
            var value = new BlendedValue();
            value.Retarget(0, now: 0, duration: 1);
            value.Retarget(10, now: 1, duration: 1);
            Assert.False(value.IsSettled(1.5));
            Assert.True(value.IsSettled(2));
        }

        [Fact]
        public void SampleCadence_LearnsTheTimersRealPeriod()
        {
            var cadence = new SampleCadence(nominal: 1);
            Assert.Equal(1, cadence.Period);

            for (int i = 0; i < 30; i++) cadence.Mark(i * 1.02);

            Assert.Equal(1.02, cadence.Period, 3);
            Assert.Equal(29 * 1.02, cadence.LastSample);
        }

        [Fact]
        public void SampleCadence_IgnoresPausesAndEarlyResamples()
        {
            var cadence = new SampleCadence(nominal: 1);
            cadence.Mark(0);
            cadence.Mark(1);
            cadence.Mark(31);      // the tab was hidden for half a minute
            cadence.Mark(31.1);    // and sampled again the moment it came back

            Assert.Equal(1, cadence.Period);
            Assert.Equal(31.1, cadence.LastSample);
        }

        // Sixty visible samples across 590 pixels: one step is ten pixels.
        private const int Visible = 60;
        private const double Width = 590;

        [Fact]
        public void Slide_TheNewestSampleReachesTheRightEdgeAsTheNextArrives()
        {
            int count = Visible + 1;
            double newest = MetricMotion.SampleX(count - 1, count, Visible, Width);
            double oldest = MetricMotion.SampleX(0, count, Visible, Width);

            Assert.Equal(Width + 10, newest + MetricMotion.SlideOffset(0, Visible, Width));
            Assert.Equal(Width, newest + MetricMotion.SlideOffset(1, Visible, Width));
            Assert.Equal(Width + 5, newest + MetricMotion.SlideOffset(0.5, Visible, Width));
            Assert.Equal(0, oldest + MetricMotion.SlideOffset(0, Visible, Width));
            Assert.Equal(-10, oldest + MetricMotion.SlideOffset(1, Visible, Width));
        }

        [Fact]
        public void Slide_ANewSampleCarriesOnFromWhereTheTraceWas()
        {
            // A full history drops its oldest sample, so every survivor moves down one index.
            int full = Visible + 1;
            for (int i = 1; i < full; i++)
                Assert.Equal(
                    MetricMotion.SampleX(i, full, Visible, Width) + MetricMotion.SlideOffset(1, Visible, Width),
                    MetricMotion.SampleX(i - 1, full, Visible, Width) + MetricMotion.SlideOffset(0, Visible, Width));

            // A history still filling keeps every sample at its index and grows by one.
            for (int i = 0; i < 10; i++)
                Assert.Equal(
                    MetricMotion.SampleX(i, 10, Visible, Width) + MetricMotion.SlideOffset(1, Visible, Width),
                    MetricMotion.SampleX(i, 11, Visible, Width) + MetricMotion.SlideOffset(0, Visible, Width));
        }

        [Fact]
        public void BlendedScale_ZoomsByASteadyRatio()
        {
            var scale = new BlendedScale();
            scale.Retarget(1, now: 0, duration: 1);
            scale.Retarget(10_000, now: 1, duration: 1);

            Assert.Equal(1, scale.At(1), 9);
            Assert.Equal(10, scale.At(1.25), 9);
            Assert.Equal(100, scale.At(1.5), 9);
            Assert.Equal(10_000, scale.At(2), 9);
        }

        [Fact]
        public void Height_ANewPeakNeverOvershootsWhereItSettles()
        {
            // An idle adapter, then a burst a million times its noise. The burst comes in past the
            // right edge while the scale counts up to it; the right edge reads the segment between.
            var history = new MetricHistory(1, Visible + 1, 0);
            history.Push(1);
            var scale = new BlendedScale();
            scale.Retarget(history.ScaleMax, now: 0, duration: 1);
            history.Push(1_000_000);
            scale.Retarget(history.ScaleMax, now: 1, duration: 1);
            double ceiling = 1 / MetricHistory.Headroom;

            double RightEdge(double progress)
            {
                double shown = scale.At(1 + progress);
                double before = MetricMotion.Height(1, shown, ceiling);
                double after = MetricMotion.Height(1_000_000, shown, ceiling);
                return before + (after - before) * progress;
            }

            double settled = RightEdge(1);
            Assert.Equal(ceiling, settled, 9);
            for (double p = 0; p <= 1; p += 0.01)
                Assert.True(RightEdge(p) <= settled + 1e-9, $"The right edge overshot at {p:0.00}: {RightEdge(p):0.000} > {settled:0.000}");
        }
    }
}
