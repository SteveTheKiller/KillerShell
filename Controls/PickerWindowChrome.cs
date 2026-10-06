using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using KillerShell.Shell;

namespace KillerShell
{
    internal static class PickerWindowChrome
    {
        // Native windows use the DWM outline as their single frame. Older Windows
        // and square themes retain the WPF outline instead of overlapping two frames.
        internal static void Apply(Window window, PickerBorder frame)
        {
            MainWindow.ApplyThemeBorder(window);
            MainWindow.ApplyWindowCorners(window, rounded: true);
            bool nativeFrame = false;
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                nativeFrame = !MainWindow.FlatChrome && handle != IntPtr.Zero
                    && DwmGetWindowAttribute(handle, 34, out int color, sizeof(int)) == 0
                    && color != -2;
            }
            catch { /* Older Windows keeps the square WPF outline. */ }
            if (nativeFrame) frame.BorderBrush = Brushes.Transparent;
            else frame.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    }
}
