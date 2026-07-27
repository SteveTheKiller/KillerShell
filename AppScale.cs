using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerShell
{
    // App-wide accessibility size, ported from KillerNotes / KillerScan / KillerPDF: a
    // LayoutTransform scale on ScaleHost (the 2-panel row - search config, splitter gap and
    // results pane, MainWindow.xaml) grows or shrinks the app content crisply. LayoutTransform
    // reflows and re-rasterizes text rather than bitmap-stretching it, which RenderTransform
    // would. The title bar and footer stay a fixed size, so the wordmark you scroll to drive
    // this (MainWindow.xaml, LogoBar) never moves. Persisted app-wide ("AppScale").
    //
    // Like KillerScan and unlike KillerNotes / KillerPDF, this needs no width bookkeeping.
    // Those two keep a sidebar column in the UNSCALED outer grid, so every saved width has to
    // be converted across a scale change. KillerShell's search-config column lives INSIDE
    // ScaleHost and its width is never persisted, so its 265 / MinWidth 200 / MaxWidth 380 are
    // logical px measured inside the transform and stay correct at any scale - the pane simply
    // grows with everything else, which is what an accessibility zoom should do.
    public partial class MainWindow
    {
        private double _appScale = 1.0;
        private const double AppScaleMin = 0.7, AppScaleMax = 2.5, AppScaleStep = 0.02;

        private void InitAppScale()
        {
            if (double.TryParse(Services.ThemeManager.GetSetting("AppScale"), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double s))
                ApplyAppScale(s);
        }

        // Roll the wheel over the wordmark: one small step per notch (fine-grained, no big jumps).
        private void LogoBar_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ApplyAppScale(_appScale + (e.Delta > 0 ? AppScaleStep : -AppScaleStep), persist: true);
            e.Handled = true;
        }

        // The wordmark is marked IsHitTestVisibleInChrome (MainWindow.xaml) so the scroll wheel
        // reaches it for the zoom above - but that also takes it out of WindowChrome's native
        // caption, so window drag and double-click-maximize are restored here by hand.
        private void LogoBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(this, new RoutedEventArgs());   // Chrome.cs
                e.Handled = true;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
                DragMove();
        }

        internal void ApplyAppScale(double scale, bool persist = false)
        {
            scale = Math.Round(Math.Max(AppScaleMin, Math.Min(AppScaleMax, scale)), 3);
            _appScale = scale;
            ScaleHost.LayoutTransform = scale == 1.0
                ? Transform.Identity
                : new ScaleTransform(scale, scale);

            // The window runs Display + ClearType (MainWindow.xaml), which pixel-snaps glyphs
            // and color-fringes them for maximum crispness at 1:1. Under a fractional scale
            // (0.96, 1.12...) those snapped stems land on partial device pixels and the results
            // list goes soft. Ideal + Grayscale positions glyphs at sub-pixel precision instead
            // and stays smooth at any scale. Same trade KillerScan makes. At exactly 1.0 we hand
            // control back to the window so the default, and by far most common, case keeps its
            // crisp text.
            if (scale == 1.0)
            {
                ScaleHost.ClearValue(TextOptions.TextFormattingModeProperty);
                ScaleHost.ClearValue(TextOptions.TextRenderingModeProperty);
            }
            else
            {
                TextOptions.SetTextFormattingMode(ScaleHost, TextFormattingMode.Ideal);
                TextOptions.SetTextRenderingMode(ScaleHost, TextRenderingMode.Grayscale);
            }

            // Footer readout, only for changes the user just made. persist is true exactly for
            // the wheel path, so a scale restored at startup (InitAppScale) applies silently -
            // nothing changed, so there is nothing to announce.
            if (persist)
            {
                ShowScaleReadout(scale);
                Services.ThemeManager.SetSetting("AppScale", scale.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        // The readout is transient: every wheel notch shows the percentage and restarts the
        // hold timer, so the footer carries it while you are zooming and drops it a beat after
        // you stop. It lives in its own label rather than the status line (KillerScan's
        // approach) so it never clobbers a running search's status.
        //
        // Normal priority, not the DispatcherTimer default of Background: the search engine's
        // callbacks are posted at Background, so during a big run a Background tick would queue
        // behind them and the readout could sit there for seconds after the last notch.
        private System.Windows.Threading.DispatcherTimer? _appScaleHide;

        private void ShowScaleReadout(double scale)
        {
            if (_appScaleHide is null)
            {
                _appScaleHide = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal)
                    { Interval = TimeSpan.FromSeconds(5) };
                _appScaleHide.Tick += (_, _) =>
                {
                    _appScaleHide!.Stop();
                    AppScaleLabel.Visibility = Visibility.Collapsed;
                };
            }

            _appScaleHide.Stop();

            // Back at exactly 100%? Collapse at once - landing on the default is its own answer.
            if (scale == 1.0) { AppScaleLabel.Visibility = Visibility.Collapsed; return; }

            AppScaleLabel.Text       = string.Format(Loc("Str_St_AppSize"), (int)Math.Round(scale * 100));
            AppScaleLabel.Visibility = Visibility.Visible;
            _appScaleHide.Start();
        }
    }
}
