using System;
using System.Windows;
using KillerFamily.Windowing;
using KillerShell.Services;

// A new window opens at the size and place of the last one closed, snapped size included, instead
// of the same large default every time. A snapped window, like a maximized one, has square
// corners, so its close button's hover is square too. Partial of MainWindow; the saved value is
// Services/SavedWindowBounds.cs.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private bool _squareCorners;

        /// <summary>
        /// Before the first show (Chrome.cs SourceInitialized): the last window's bounds, kept
        /// inside the monitor they land on. A window already opening maximized or minimized (a
        /// handed-off program that asked for it, Start with Windows) keeps that state.
        /// </summary>
        private void RestoreWindowBounds()
        {
            if (DemoMode) return;
            if (!SavedWindowBounds.TryParse(Services.ThemeManager.GetSetting(SavedWindowBounds.Setting),
                                            out Rect bounds, out bool maximized)) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.X;
            Top = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
            WorkAreaFit.Fit(this, keepMargin: false);
            if (maximized && WindowState == WindowState.Normal) WindowState = WindowState.Maximized;
        }

        /// <summary>
        /// On close: where this window is, snapped size included (Left, Top and Actual* follow the
        /// snap; RestoreBounds is the size before it), or its ordinary size when maximized.
        /// </summary>
        private void SaveWindowBounds()
        {
            if (DemoMode) return;
            try
            {
                Rect bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, ActualWidth, ActualHeight)
                    : RestoreBounds;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
                Services.ThemeManager.SetSetting(SavedWindowBounds.Setting,
                    SavedWindowBounds.Format(bounds, WindowState == WindowState.Maximized));
            }
            catch { /* a window size is never worth a failed close */ }
        }

        /// <summary>
        /// Windows draws a maximized or snapped window square. A snapped window is Normal but sits
        /// somewhere other than its ordinary bounds, which RestoreBounds keeps.
        /// </summary>
        private bool IsSnapped()
        {
            if (WindowState != WindowState.Normal || !IsLoaded) return false;
            Rect normal = RestoreBounds;
            if (normal.IsEmpty) return false;
            return Math.Abs(normal.Left - Left) > 1 || Math.Abs(normal.Top - Top) > 1 ||
                   Math.Abs(normal.Width - ActualWidth) > 1 || Math.Abs(normal.Height - ActualHeight) > 1;
        }

        /// <summary>Square window corners and close hover while maximized or snapped, rounded otherwise.</summary>
        private void SyncWindowCorners(bool force = false)
        {
            bool square = WindowState == WindowState.Maximized || IsSnapped();
            if (!force && square == _squareCorners) return;
            _squareCorners = square;
            ApplyWindowCorners(this, rounded: !square && WindowState == WindowState.Normal);
            SyncElevationHaloCorners();   // Elevation.cs - the admin ring squares with the window
            if (CloseBtn == null) return;
            if (square) CloseBtn.Resources["CaptionCloseCornerRadius"] = new CornerRadius(0);
            else CloseBtn.Resources.Remove("CaptionCloseCornerRadius");
        }
    }
}
