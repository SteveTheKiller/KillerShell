using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// The Performance Monitor's motion between samples: the third part of PerformanceMonitorControl,
// beside the counters (PerformanceMonitorControl.cs) and the dashboard (PerformanceMonitorLayout.cs).
//
// The counters are still read once a second. What this adds is the second in between: every
// readout counts from what it shows to its new sample, and every graph slides one step left as
// its newest point comes in past the right edge, so the tab moves continuously instead of ticking
// like a clock. The arithmetic is Services/MetricMotion.cs.
//
// Driven per composition frame, like Controls/Anim.cs and for the same reason: a Timeline
// animation is suppressed outright when Windows animations are off or over a remote session, and
// this tab is often watched over one. The frame callback is hooked only for the second after a
// sample and only while the tab is on screen. A hidden or minimized tab writes each new reading
// straight in, the way it always did, and does no per-frame work at all.
namespace KillerShell.Tools
{
    internal sealed partial class PerformanceMonitorControl
    {
        private readonly Services.SampleCadence _cadence = new(RefreshInterval.TotalSeconds);

        // Every counted readout, keyed by the element it writes to: a TextBlock for a number, a
        // logical processor's fill for its load, a memory bar column for its share.
        private sealed class Readout
        {
            internal readonly Services.BlendedValue Value = new();
            internal Action<double> Show = null!;
            internal bool Settled;
        }

        private readonly Dictionary<object, Readout> _readouts = [];
        private bool _framesHooked;
        private TimeSpan _lastFrame;

        private static double MotionNow() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>On screen and worth moving: loaded, visible, in a window that is not
        /// minimized, and not shut down.</summary>
        private bool CanAnimate => IsLoaded && IsVisible && !_lifetimeCts.IsCancellationRequested
            && Window.GetWindow(this) is { WindowState: not WindowState.Minimized };

        /// <summary>Counts a number readout toward its new sample.</summary>
        private void CountTo(TextBlock block, double value, Func<double, string> format)
            => Blend(block, value, shown =>
            {
                string text = format(shown);
                if (block.Text != text) block.Text = text;   // an unchanged string costs no layout
            });

        private void CountField(MetricTile tile, int index, double value, Func<double, string> format)
        {
            if (index < tile.FieldValueBlocks.Length) CountTo(tile.FieldValueBlocks[index], value, format);
        }

        private void BlendColumn(ColumnDefinition column, double share)
            => Blend(column, share, shown => column.Width = new GridLength(shown, GridUnitType.Star));

        private void Blend(object target, double value, Action<double> show)
        {
            if (!_readouts.TryGetValue(target, out var readout)) _readouts[target] = readout = new Readout();
            readout.Value.Retarget(value, _cadence.LastSample, _cadence.Period);
            readout.Show = show;
            readout.Settled = false;
            // Off screen there is nothing to count through, so the reading lands at once.
            show(CanAnimate ? readout.Value.At(_cadence.LastSample) : value);
        }

        /// <summary>A readout with no number right now. Its count is forgotten, so a reading that
        /// comes back later lands as is instead of counting up from a stale one.</summary>
        private void ShowDash(TextBlock block)
        {
            _readouts.Remove(block);
            block.Text = "-";
        }

        private void ForgetReadouts(MetricTile tile)
        {
            if (tile.TileSummaryText != null) _readouts.Remove(tile.TileSummaryText);
            foreach (var block in tile.LegendValueBlocks) _readouts.Remove(block);
            foreach (var block in tile.FieldValueBlocks) _readouts.Remove(block);
        }

        /// <summary>After every panel has its new sample: move toward it over the next second if
        /// the tab is on screen, otherwise land on it now.</summary>
        private void BeginMotion()
        {
            if (CanAnimate) HookFrames();
            else SettleMotion();
        }

        private void HookFrames()
        {
            if (_framesHooked) return;
            _framesHooked = true;
            CompositionTarget.Rendering += OnFrame;
        }

        private void UnhookFrames()
        {
            if (!_framesHooked) return;
            _framesHooked = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            // Rendering can be raised more than once for the same frame; only the first moves anything.
            if (e is RenderingEventArgs frame)
            {
                if (frame.RenderingTime == _lastFrame) return;
                _lastFrame = frame.RenderingTime;
            }

            if (!CanAnimate)
            {
                // Minimized, hidden or closed partway through: finish on the new readings and stop.
                UnhookFrames();
                SettleMotion();
                return;
            }

            double now = MotionNow();
            AdvanceMotion(now);
            // Everything started on the latest sample and lasts one period. Once that has passed
            // nothing moves until the next sample hooks the frames again.
            if (now >= _cadence.LastSample + _cadence.Period) UnhookFrames();
        }

        private void SettleMotion() => AdvanceMotion(double.PositiveInfinity);

        private void AdvanceMotion(double now)
        {
            foreach (var readout in _readouts.Values)
            {
                if (readout.Settled) continue;
                readout.Show(readout.Value.At(now));
                readout.Settled = readout.Value.IsSettled(now);
            }

            foreach (var tile in _tiles)
            {
                if (tile.State is CpuState cs) cs.AggregateGraph.Advance(now);
                foreach (var graph in tile.BigGraphs) graph.Advance(now);
            }
            _sumCpu?.Trace.Advance(now);
            _sumRam?.Trace.Advance(now);
            _sumDisk?.Trace.Advance(now);
            _sumNet?.Trace.Advance(now);
            _sumGpu?.Trace.Advance(now);
        }
    }
}
