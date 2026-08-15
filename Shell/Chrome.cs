using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

// KillerUI / Grunge - custom window chrome. Partial of MainWindow.
// MainWindow.xaml must name: RootGrid (Opacity=0), MinimizeBtn/MaximizeBtn/CloseBtn,
// ResizeGrip, and any of GrainBrush/TitleGrainBrush/ToolbarGrainBrush/StatusGrainBrush/FlyoutGrainBrush.
// Ctor calls: SourceInitialized += MainWindow_SourceInitialized; ApplyGrainTexture(); Loaded += (_,_) => FadeInContent();
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Pin the title bar's ROW, and the WindowChrome caption band, to the theme's
        /// TitleBarHeight. Called from the ctor and again on every theme change, because the two
        /// flat/non-flat captions are different heights (22 vs 36).
        ///
        /// Both halves are needed and neither works alone:
        ///   - The ROW: a hardcoded 36 with a 22px caption Grid inside it centers the caption and
        ///     leaves 7px of frame gray above and below it. That gap is INSIDE the row, so no
        ///     amount of trimming the window's padding could reach it.
        ///   - CaptionHeight: WindowChrome's drag band is measured from the top of the WINDOW and
        ///     is a plain constant. Left at 36 over a 22px caption it would keep claiming 14px of
        ///     the tab strip below, so the top of every tab would drag the window instead of
        ///     selecting the tab.
        ///
        /// Code rather than markup: a RowDefinition takes a GridLength, not a Double, and
        /// WindowChrome is not in the visual tree so a DynamicResource on it never resolves.
        /// </summary>
        private void SyncTitleBarMetrics()
        {
            double h = TryFindResource("TitleBarHeight") is double d && d > 0 ? d : 36.0;

            if (TitleRow != null) TitleRow.Height = new GridLength(h);

            // Same treatment for the footer. 98SE's sunken cells need more room than the 24px the
            // rounded themes use - the cell loses 2px a side to FooterCellMargin and the bevel
            // borders paint OVER the content rather than reserving space, so at 24 the status and
            // version text ran into the cell edges.
            double fh = TryFindResource("FooterHeight") is double fd && fd > 0 ? fd : 24.0;
            if (FooterRow != null) FooterRow.Height = new GridLength(fh);

            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome != null) chrome.CaptionHeight = h;
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ApplyWindowCorners(this, rounded: WindowState == WindowState.Normal);
            ApplyThemeBorder(this);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND      = 2;
        private const int DWMWA_BORDER_COLOR = 34;

        /// <summary>Tints the Win11 DWM frame border to the theme's PaneBorderBrush, so the
        /// 1px window outline follows the theme instead of staying system gray (the gray
        /// frame appears as soon as a borderless window opts into DWM rounded corners).
        /// Call at SourceInitialized and again after every theme change.</summary>
        internal static void ApplyThemeBorder(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                // AppBorderBrush is the frame's OWN key, and every palette picks it deliberately -
                // Sepulchre's #44585b desaturated teal against a #4d3d2b brown pane border, for
                // instance. It was briefly switched to PaneBorderBrush on the theory that the
                // vendored palettes used this key for something else; they do not, and the switch
                // painted Sepulchre's frame brown. PaneBorderBrush stays as
                // the fallback for a palette that declines to state one.
                if ((Application.Current.TryFindResource("AppBorderBrush")
                     ?? Application.Current.TryFindResource("PaneBorderBrush")) is SolidColorBrush b)
                {
                    // COLORREF is 0x00BBGGRR
                    int colorref = b.Color.R | (b.Color.G << 8) | (b.Color.B << 16);
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
                }
            }
            catch { /* pre-Win11: attribute unsupported */ }
        }

        /// <summary>
        /// Sets the DWM rounded-corner preference for <paramref name="w"/>. Static and taking an
        /// explicit Window, like ApplyThemeBorder above, so every other themed popup window in
        /// the family (FileDialog, FolderPickerDialog, ...) can call it too rather than only
        /// MainWindow ever getting rounded corners - and, on Windows 11, the standard window
        /// drop shadow along with it: a chromeless (WindowStyle="None") popup with no corner
        /// preference set renders with NEITHER a rounded frame NOR a shadow, which is exactly
        /// what FileDialog/FolderPickerDialog looked like before this - they had ApplyThemeBorder
        /// wired in already but never this.
        /// </summary>
        /// <summary>
        /// True when the active theme is FLAT - 98SE. A flat theme draws its own hard frame and
        /// must never get Win11 rounded corners: the rounding cut the corners off the bevel and
        /// left the DWM frame curving around a square window. Read from the
        /// palette rather than the theme name, so any future flat theme gets it for free.
        /// </summary>
        internal static bool FlatChrome =>
            Application.Current?.TryFindResource("UseDialogCaption") is bool f && f;

        internal static void ApplyWindowCorners(Window w, bool rounded)
        {
            if (FlatChrome) rounded = false;
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int pref = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* pre-Win11: no rounded-corner API */ }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            ApplyWindowCorners(this, rounded: WindowState == WindowState.Normal);
            // Segoe MDL2: E923 restore (when maximized) / E922 maximize. Built from a (char)
            // cast, never typed as a literal PUA character - literal glyphs do not survive
            // tooling (family-wide rule).
            if (MaximizeBtn != null)
            {
                int glyph = WindowState == WindowState.Maximized ? 0xE923 : 0xE922;
                MaximizeBtn.Content = ((char)glyph).ToString();
            }
        }

        private void FadeInContent() => Anim.FadeIn(RootGrid);

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_ERASEBKGND    = 0x0014;
        private const int WM_NCRBUTTONUP   = 0x00A5;
        private const int HTCAPTION        = 2;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        // Another KillerShell window handing us a tab it was dragged onto (TabHandoff.cs) -
        // the reverse of tearing one out (TabTearOut.cs). Windows itself defines WM_COPYDATA's
        // value; it does not belong beside the window-chrome messages above by meaning, only by
        // being the one other message this WndProc has to answer.
        private const int WM_COPYDATA = 0x004A;

        // Broadcast by Windows when an environment variable changes at User or Machine scope,
        // with lParam pointing at the string "Environment". A process only ever gets a COPY of
        // the environment at launch, so without this a PATH change made after KillerShell
        // started is invisible to it and to every shell it spawns until the app is restarted.
        // Installing a CLI tool and finding the terminal still cannot see it is the case that
        // matters (ShellEnv.cs, RefreshEnvironmentPath).
        private const int WM_SETTINGCHANGE = 0x001A;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SETTINGCHANGE)
            {
                string? area = null;
                try { if (lParam != IntPtr.Zero) area = Marshal.PtrToStringAuto(lParam); }
                catch { }
                if (string.Equals(area, "Environment", StringComparison.OrdinalIgnoreCase))
                    RefreshEnvironmentPath();   // ShellEnv.cs
                // Deliberately not marked handled: this is an observation, and WPF and any other
                // hook on this window are entitled to see the broadcast too.
            }
            if (msg == WM_ERASEBKGND)
            {
                // KillerPDF's anti-flash trick: WPF paints the whole client area itself, so
                // let nothing erase the background to a flat fill during a resize - that
                // erase is the white flash. Claim the message and report success.
                handled = true;
                return new IntPtr(1);
            }
            if (msg == WM_NCRBUTTONUP && (int)wParam.ToInt64() == HTCAPTION)
            {
                // Right-click on the caption: the native system menu can't be themed,
                // so suppress it and show our own Grunge menu instead.
                Dispatcher.BeginInvoke((Action)ShowCaptionMenu);
                handled = true;
                return IntPtr.Zero;
            }
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            if (msg == WM_COPYDATA)
            {
                handled = true;
                return HandleCopyData(lParam);   // TabHandoff.cs
            }
            if (msg == (int)WM_KS_TABHOVER)   // TabHandoff.cs
            {
                handled = true;
                HandleTabHover(wParam, lParam);   // TabHandoff.cs
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        // Themed replacement for the caption's system menu (minimize / maximize-restore /
        // close). Uses the implicit ContextMenu/MenuItem styles, so it grains and themes.
        private void ShowCaptionMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var mini = new System.Windows.Controls.MenuItem { Header = Loc("Str_Menu_Minimize") };
            mini.Click += (_, _) => WindowState = WindowState.Minimized;
            menu.Items.Add(mini);

            var maxi = new System.Windows.Controls.MenuItem
                { Header = Loc(WindowState == WindowState.Maximized ? "Str_Menu_Restore" : "Str_Menu_Maximize") };
            maxi.Click += (_, _) => WindowState =
                WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            menu.Items.Add(maxi);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var close = new System.Windows.Controls.MenuItem { Header = Loc("Str_Menu_Close") };
            close.Click += (_, _) => Close();
            menu.Items.Add(close);

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        // WM_GETMINMAXINFO is the OS asking how big and how SMALL this window may be. Because
        // the reply below sets handled = true, whatever is left unset here is what the window
        // gets - Windows' own answer is discarded.
        //
        // The minimum used to be left unset, and that is why the window could be dragged far
        // below MinWidth. WPF's MinWidth / MinHeight are not consulted by the native resize at
        // all on a WindowStyle="None" + WindowChrome window: the OS resizes the frame and asks
        // only this question. Dragged narrow enough, the content became wider than the frame and
        // the title bar and footer were clipped off the right - which looked like a layout bug
        // and was really this.
        //
        // No longer static: it needs the window's own MinWidth and DPI.
        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            // MinWidth is in device-independent units and this struct is in physical pixels, so
            // it has to go through the window's own DPI - on a 150% display the two differ by
            // half again, and the raw number would let the window get a third smaller than asked.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth  * dpi.DpiScaleX);
            mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref info);
                RECT work = info.rcWork;
                RECT mon = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
            }

            // Written back unconditionally. It used to happen only inside the monitor branch, so
            // on the rare occasion MonitorFromWindow failed, every value computed here was
            // thrown away.
            Marshal.StructureToPtr(mmi, lParam, true);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>Work-area width of the monitor this window is currently on, in DIP. Same
        /// GetMonitorInfo call WmGetMinMaxInfo already makes for the OS-level max track size,
        /// reused here so DualPane.cs (F10 open, gutter drag) can ask "is there room to grow the
        /// window" without a second, different way of asking Windows the same question. Returns
        /// double.MaxValue on failure so a lookup miss never blocks a grow/split decision - the
        /// caller's own fallback (split-in-place) is a safe default either way.</summary>
        internal double MonitorWorkAreaWidthDip()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return double.MaxValue;
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return double.MaxValue;
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(monitor, ref info)) return double.MaxValue;
                double workWidthPx = Math.Abs(info.rcWork.right - info.rcWork.left);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                return workWidthPx / dpi.DpiScaleX;
            }
            catch
            {
                return double.MaxValue;
            }
        }

        /// <summary>How much further this window's RIGHT edge could move before leaving its
        /// monitor's work area, in DIP - what DualPane.cs's F10-grow decision actually needs
        /// (F10 animated the second pane clean off screen on a window snapped to the right half
        /// of the monitor). MonitorWorkAreaWidthDip alone answers "would the
        /// FINAL width fit somewhere on this monitor", which was true even here - a snapped-right
        /// window's ActualWidth is only about half the monitor's work width, so `ActualWidth +
        /// growth` cleared that check easily. But growing happens IN PLACE (Left never moves,
        /// only the right edge does), and this window's Left already sits at roughly the
        /// midpoint, so there was never really room for the right edge to move without leaving
        /// the monitor - MonitorWorkAreaWidthDip had no way to know that because it never looked
        /// at Left at all. This measures the ACTUAL gap between the right edge and the work
        /// area's own right edge instead. Same GetMonitorInfo call and DPI conversion as
        /// MonitorWorkAreaWidthDip, same double.MaxValue-on-failure fallback.</summary>
        internal double MonitorRoomToGrowRightDip()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return double.MaxValue;
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return double.MaxValue;
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(monitor, ref info)) return double.MaxValue;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                double workRightDip = info.rcWork.right / dpi.DpiScaleX;
                return workRightDip - (Left + ActualWidth);
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private void ResizeGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (WindowState != WindowState.Normal) return;
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTBOTTOMRIGHT), IntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
            => Close();

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        // Film grain: bright + dark specks (~33% density) so it reads on dark and light themes.
        private void ApplyGrainTexture()
        {
            const int size = 256;
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4];
            var rng = new Random(1337);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (rng.Next(3) != 0) continue;
                bool bright = rng.Next(2) == 0;
                byte v = bright ? (byte)rng.Next(190, 255) : (byte)rng.Next(0, 50);
                byte a = (byte)rng.Next(35, 95);
                pixels[i]     = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = a;
            }
            bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);

            foreach (var name in new[] { "GrainBrush", "TitleGrainBrush", "ToolbarGrainBrush", "StatusGrainBrush", "FlyoutGrainBrush" })
                if (FindName(name) is ImageBrush ib) ib.ImageSource = bmp;

            var grainTile = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new System.Windows.Rect(0, 0, size, size),
                Stretch = Stretch.None
            };
            grainTile.Freeze();
            Application.Current.Resources["GrainTileBrush"] = grainTile;
        }
    }
}
