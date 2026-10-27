using System;
using System.Windows;

namespace KillerShell.Shell
{
    // APP-OWNED: KillerShell's settings for the kit's app size (Shell/AppScale.cs). The readout
    // shows in its own AppScaleLabel, which takes the status line's place while sizing, and the
    // panes re-snap their edges after every step.
    public partial class MainWindow
    {
        private System.Windows.Threading.DispatcherTimer? _appScaleLabelHide;

        partial void ConfigureAppScale(ref double min, ref double max, ref double step, ref bool smoothText)
        {
            min = 0.7;
            max = 2.5;
            step = 0.02;
            smoothText = true;
        }

        partial void ApplyAppScaleTo(double scale, ref bool handled)
        {
            LeftPane.QueueSnap();    // FilePane.xaml.cs: the panes' edges move to new device pixels
            RightPane.QueueSnap();
        }

        partial void ShowAppScaleReadout(double scale, ref bool handled)
        {
            handled = true;
            if (_appScaleLabelHide is null)
            {
                _appScaleLabelHide = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal)
                    { Interval = TimeSpan.FromSeconds(5) };
                _appScaleLabelHide.Tick += (_, _) =>
                {
                    _appScaleLabelHide!.Stop();
                    AppScaleLabel.Visibility = Visibility.Collapsed;
                    StatusText.Visibility    = Visibility.Visible;
                };
            }

            _appScaleLabelHide.Stop();

            if (scale == 1.0)
            {
                AppScaleLabel.Visibility = Visibility.Collapsed;
                StatusText.Visibility    = Visibility.Visible;
                return;
            }

            AppScaleLabel.Text       = string.Format(Loc("Str_St_AppSize"), (int)Math.Round(scale * 100));
            AppScaleLabel.Visibility = Visibility.Visible;
            StatusText.Visibility    = Visibility.Collapsed;
            _appScaleLabelHide.Start();
        }

        internal void ShowTerminalTextSize(double fontSize)
            => StatusText.Text = string.Format(Loc("Str_Status_TextSize"),
                                               (int)Math.Round(fontSize / 13.0 * 100));
    }
}
