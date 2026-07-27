using System.Windows;
using System.Windows.Input;

// Results density. Partial of MainWindow.
//
// Five levels, cycled from the rail button or stepped with the wheel over it - the same
// gesture KillerNotes uses for its sidebar, because a hand trained on one should not have to
// learn the other:
//   0 = Roomy       - looser than the app has ever been, for a big screen across the room
//   1 = Comfortable - the original spacing, and where a fresh install starts
//   2 = Compact     - about half the padding
//   3 = Tight       - most of it gone
//   4 = Minimal     - as many rows as the pane will physically hold
//
// Side padding steps with the rest: a level that only pulled rows closer vertically would
// still waste the same margin down both edges, which is the width you actually notice in a
// half-width pane.
//
// It acts on all three views at once, not just the one showing. Density is a preference about
// how much you want on screen, not a property of a layout, and having to set it three times
// because you switched view would be the wrong shape entirely.
//
// The numbers themselves live on ResultsViewState (ResultsView.cs) where the templates already
// bind, so changing the level repaints rather than re-listing - which matters when the list can
// hold six figures of rows.
namespace KillerShell
{
    public partial class MainWindow
    {
        private const string SetDensity = "ResultsDensity";

        private static readonly string[] DensityStatusKeys =
            ["Str_St_DensityRoomy", "Str_St_DensityFull", "Str_St_DensityCompact",
             "Str_St_DensityTight", "Str_St_DensityMin"];

        private void InitDensity()
        {
            if (int.TryParse(Services.ThemeManager.GetSetting(SetDensity), out int d))
                ResultsViewState.Current.Density = d;   // clamps itself
        }

        /// <summary>Click steps one tighter, Roomy through Minimal, then back round to Roomy.</summary>
        private void Density_Click(object sender, RoutedEventArgs e)
            => ApplyDensity((ResultsViewState.Current.Density + 1) % ResultsViewState.DensityLevels);

        /// <summary>Wheel up is roomier, wheel down is tighter - the direction the list moves.</summary>
        private void Density_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ApplyDensity(ResultsViewState.Current.Density + (e.Delta > 0 ? -1 : 1));
            e.Handled = true;   // never let it fall through and scroll the results underneath
        }

        private void ApplyDensity(int level)
        {
            var s = ResultsViewState.Current;
            s.Density = level;
            Services.ThemeManager.SetSetting(SetDensity, s.Density.ToString());

            // Named rather than silent: at a glance Compact and Minimal differ by a few pixels
            // per row, and without the caption a wheel notch reads as nothing having happened.
            if (_active != null) SetTabStatusKey(_active, DensityStatusKeys[s.Density]);
        }
    }
}
