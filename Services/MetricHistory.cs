using System;
using System.Collections.Generic;

namespace KillerShell.Services
{
    internal sealed class MetricHistory
    {
        private readonly int _capacity;
        private readonly bool _autoScale;
        private readonly List<double>[] _series;

        internal IReadOnlyList<double>[] Series => _series;
        internal double ScaleMax { get; private set; }

        internal MetricHistory(int seriesCount, int capacity, double fixedScaleMax)
        {
            _capacity = Math.Max(1, capacity);
            _autoScale = fixedScaleMax <= 0;
            ScaleMax = fixedScaleMax > 0 ? fixedScaleMax : 1;
            _series = new List<double>[Math.Max(1, seriesCount)];
            for (int i = 0; i < _series.Length; i++) _series[i] = [];
        }

        internal void Push(params double[] values)
        {
            for (int i = 0; i < values.Length && i < _series.Length; i++)
            {
                List<double> samples = _series[i];
                samples.Add(values[i]);
                if (samples.Count > _capacity) samples.RemoveRange(0, samples.Count - _capacity);
            }

            if (!_autoScale) return;
            double maximum = 1;
            foreach (List<double> samples in _series)
                foreach (double sample in samples)
                    if (sample > maximum) maximum = sample;
            ScaleMax = maximum * 1.2;
        }
    }
}
