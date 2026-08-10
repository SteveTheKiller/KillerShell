using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;

namespace KillerShell
{
    // Shared by ProcessDetailsDialog, ServiceDetailsDialog and EventDetailsDialog: these dialogs
    // need maximums so they never open larger than the MONITOR the user is on (the monitor, not
    // another app window). Each of those dialogs already
    // carries a fixed MaxHeight in its own XAML, but a fixed pixel value can still exceed a
    // small/laptop screen's actual work area - one helper here so the clamp logic is not tripled
    // across three files with three chances to drift.
    internal static class DialogScreenClamp
    {
        // Call from SourceInitialized - the same place each of these dialogs already runs
        // ApplyRoundedCorners/ApplyThemeBorder. The window has a native handle by then, and
        // Owner is already set (the callers assign it via an object initializer before
        // ShowDialog(), which happens before the constructor's own SourceInitialized handler
        // fires). Doing this before the dialog is shown means SizeToContent="Height" measures
        // against the real clamp from the start, instead of opening oversized and snapping down
        // a frame later.
        public static void Apply(Window dialog)
        {
            var owner = dialog.Owner;
            if (owner == null) return;

            try
            {
                var ownerHandle = new WindowInteropHelper(owner).Handle;
                if (ownerHandle == IntPtr.Zero) return;

                // FromHandle, not the primary screen - the dialog should clamp to whichever
                // monitor the KillerShell main window is actually on, not always the primary one.
                Screen screen = Screen.FromHandle(ownerHandle);
                System.Drawing.Rectangle work = screen.WorkingArea; // physical pixels

                // WorkingArea is physical pixels; WPF sizes are device-independent units, so this
                // has to go through the owner's own DPI or a 150%/200% scaled monitor clamps the
                // dialog far too small (same physical-pixel/DIP conversion Shell/Chrome.cs
                // WmGetMinMaxInfo already does for MinWidth/MinHeight, just the other direction).
                DpiScale dpi = VisualTreeHelper.GetDpi(owner);
                double workWidth = work.Width / dpi.DpiScaleX;
                double workHeight = work.Height / dpi.DpiScaleY;

                // 90% of the work area - flush against every screen edge looks wrong. Math.Min
                // against the dialog's own XAML MaxHeight/MaxWidth means a huge 4K monitor never
                // lets the dialog balloon past what already looked reasonable, while a small or
                // laptop screen now constrains it tighter than the fixed constant alone did.
                dialog.MaxWidth = Math.Min(dialog.MaxWidth, workWidth * 0.9);
                dialog.MaxHeight = Math.Min(dialog.MaxHeight, workHeight * 0.9);
            }
            catch
            {
                // Screen/DPI lookup is best-effort - a failure here should never block the dialog
                // from opening with just the XAML-declared fixed MaxHeight.
            }
        }
    }
}
