using System;

// How the Performance Monitor's once-a-second samples become continuous motion. The counters are
// still read once a second; what changes is that each new reading is reached over the second
// that follows it instead of landing all at once. A number counts from what it shows now to the
// new reading, and a graph slides one step left while its newest point comes in from beyond the
// right edge, so the trace's right edge passes through every value between two samples. Both
// run on the same clock, so a headline always reads the height of its graph's right edge. An
// auto-scaled graph's top counts as well, by ratio, so a burst zooms the graph out over its
// second instead of flattening everything already drawn.
//
// Pure arithmetic with times in seconds, no WPF: Tools/PerformanceMonitorMotion.cs drives it once
// per rendered frame, and only while the tab is on screen.
namespace KillerShell.Services
{
    internal static class MetricMotion
    {
        /// <summary>How far a blend that started at <paramref name="startedAt"/> has come: 0 at
        /// the sample, 1 once <paramref name="duration"/> has passed, and held at 1 after that.</summary>
        internal static double Progress(double now, double startedAt, double duration)
        {
            if (duration <= 0) return 1;
            return Math.Max(0, Math.Min(1, (now - startedAt) / duration));
        }

        /// <summary>
        /// Where sample <paramref name="index"/> of <paramref name="count"/> (0 is the oldest) is
        /// laid out when its slide begins: the newest sits one step past the right edge, so a full
        /// history of <paramref name="visibleSamples"/> plus one starts with its oldest on the left
        /// edge. The points are laid out here once per sample; SlideOffset moves them all.
        /// </summary>
        internal static double SampleX(int index, int count, int visibleSamples, double width)
            => (visibleSamples - count + index + 1) * Step(visibleSamples, width);

        /// <summary>How far the laid-out points have moved: none at the sample, one whole step to
        /// the left by the time the next one arrives.</summary>
        internal static double SlideOffset(double progress, int visibleSamples, double width)
            => -progress * Step(visibleSamples, width);

        private static double Step(int visibleSamples, double width)
            => visibleSamples > 1 ? width / (visibleSamples - 1) : 0;

        /// <summary>
        /// A sample's height as a fraction of the graph, at the scale on screen. Nothing is drawn
        /// above <paramref name="ceiling"/>: for an auto-scaled graph that is the headroom line its
        /// highest sample settles on, so a new peak that arrives before the scale has counted up
        /// to it waits on that line instead of reaching higher and dropping back.
        /// </summary>
        internal static double Height(double sample, double scale, double ceiling)
            => scale > 0 ? Math.Min(ceiling, sample / scale) : 0;
    }

    /// <summary>
    /// An auto-scaled graph's top on its way to a new value, counted by ratio rather than by
    /// difference. A top that grows from a kilobyte a second to ten megabytes zooms out at a
    /// steady rate; counted by difference, the old trace would be flattened against the axis in
    /// the first frame, and a shrinking top would hold still and then spring up at the end.
    /// </summary>
    internal sealed class BlendedScale
    {
        private readonly BlendedValue _log = new();

        internal void Retarget(double target, double now, double duration)
            => _log.Retarget(Math.Log(Math.Max(target, double.Epsilon)), now, duration);

        internal double At(double now) => Math.Exp(_log.At(now));
    }

    /// <summary>
    /// When the latest sample was taken and how long the next one usually takes. A blend lasts
    /// one measured period, not the nominal second: the refresh timer restarts after each sample
    /// is read, so the real gap runs a little long, and a blend that finished on the nominal
    /// second would stop and wait for that difference every time.
    /// </summary>
    internal sealed class SampleCadence
    {
        private readonly double _nominal;
        private bool _marked;

        internal SampleCadence(double nominal)
        {
            _nominal = nominal;
            Period = nominal;
        }

        internal double Period { get; private set; }
        internal double LastSample { get; private set; }

        internal void Mark(double now)
        {
            // A gap far from the nominal interval is a pause (the tab was hidden, the window was
            // busy) or the immediate resample when the tab comes back, not the timer's rhythm.
            double gap = now - LastSample;
            if (_marked && gap > _nominal * 0.5 && gap < _nominal * 2)
                Period += (gap - Period) * 0.25;
            LastSample = now;
            _marked = true;
        }
    }

    /// <summary>
    /// One displayed number on its way to its latest sample. A new sample always starts from the
    /// value on screen at that moment, so a reading that arrives early or late never jumps.
    /// </summary>
    internal sealed class BlendedValue
    {
        private double _from, _to, _startedAt, _duration;
        private bool _hasValue;

        /// <summary>The first sample lands as is: there is nothing on screen yet to count from.</summary>
        internal void Retarget(double target, double now, double duration)
        {
            _from = _hasValue ? At(now) : target;
            _to = target;
            _startedAt = now;
            _duration = duration;
            _hasValue = true;
        }

        /// <summary>Exactly the target once settled, so a settled readout or scale compares equal
        /// to the next sample of the same value and nothing is re-laid for a rounding error.</summary>
        internal double At(double now)
        {
            double progress = MetricMotion.Progress(now, _startedAt, _duration);
            return progress >= 1 ? _to : _from + (_to - _from) * progress;
        }

        internal bool IsSettled(double now) => MetricMotion.Progress(now, _startedAt, _duration) >= 1;
    }
}
