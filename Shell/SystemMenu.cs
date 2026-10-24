using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using KillerFamily.Accessibility;

// ============================================================
// THEMED WINDOWS SYSTEM MENU (the family kit's KillerUI Shell/SystemMenu.cs)
//
// WindowChrome makes the title bar a real native caption, so Windows answers a caption
// right-click (and Alt+Space) with its own white HMENU, which no WPF style can reach. Both
// messages are swallowed here and a themed ContextMenu shows instead, with Windows' own items:
// Restore, Move, Size, Minimize, Maximize and Close. Each posts back the WM_SYSCOMMAND the native
// menu would have sent, so Move and Size hand off to Windows' own keyboard move and size loops.
// As in Windows' menu, the item a double-click would pick is bold, what cannot apply is grayed
// and Close shows Alt+F4.
//
// TRAP (KillerPDF, 2026-07-31): an Absolute-placement ContextMenu with no PlacementTarget has no
// inheritance context and sees only APP-level implicit styles. ShowSystemMenu sets
// PlacementTarget = this, which supplies the resource context without moving the menu.
// ============================================================
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private const int WM_SYSCOMMAND = 0x0112;

        private const int SC_SIZE     = 0xF000;
        private const int SC_MOVE     = 0xF010;
        private const int SC_MINIMIZE = 0xF020;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SC_CLOSE    = 0xF060;
        private const int SC_KEYMENU  = 0xF100;
        private const int SC_RESTORE  = 0xF120;

        private ContextMenu? _sysMenu;

        /// <summary>
        /// Called first in WndProc (Chrome.cs). True when the message raised the system menu and
        /// the native one is suppressed.
        /// </summary>
        private bool TryHandleSystemMenu(int msg, IntPtr wParam, IntPtr lParam)
        {
            // Right-click on the caption.
            if (msg == WM_NCRBUTTONUP && wParam.ToInt64() == HTCAPTION)
            {
                ShowSystemMenu(ScreenPointFromLParam(lParam));
                return true;
            }

            // Alt+Space. SC_KEYMENU with a space is the keyboard route to the same menu; the low
            // nibble carries state, so it is masked off.
            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU && lParam.ToInt64() == ' ')
            {
                ShowSystemMenu(null);
                return true;
            }

            return false;
        }

        /// <summary>lParam of a non-client mouse message packs screen coordinates as two shorts.</summary>
        private static Point ScreenPointFromLParam(IntPtr lParam)
        {
            int v = unchecked((int)lParam.ToInt64());
            return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        }

        private void ShowSystemMenu(Point? screenPoint)
        {
            _sysMenu ??= BuildSystemMenu();

            bool maximized = WindowState == WindowState.Maximized;
            // As in Windows: Restore only from maximized, Maximize only from normal, no Move or Size
            // while maximized, and the default item in bold: Restore when maximized, Close otherwise.
            int defaultCommand = maximized ? SC_RESTORE : SC_CLOSE;
            foreach (object o in _sysMenu.Items)
            {
                if (o is not MenuItem mi || mi.Tag is not int cmd) continue;
                mi.IsEnabled = cmd switch
                {
                    SC_RESTORE => maximized,
                    SC_MAXIMIZE => !maximized,
                    SC_MOVE or SC_SIZE => !maximized,
                    _ => true,
                };
                mi.FontWeight = cmd == defaultCommand ? FontWeights.Bold : FontWeights.Normal;
            }

            _sysMenu.PlacementTarget = this;
            _sysMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
            if (screenPoint is { } p)
            {
                // Screen pixels to DIPs, so the menu lands under the pointer at any DPI.
                if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
                    p = target.TransformFromDevice.Transform(p);
                _sysMenu.HorizontalOffset = p.X;
                _sysMenu.VerticalOffset = p.Y;
            }
            else
            {
                // Keyboard route: under the window's top-left corner, where Windows puts it.
                _sysMenu.HorizontalOffset = Left + 8;
                _sysMenu.VerticalOffset = Top + 36;
            }

            _sysMenu.IsOpen = true;
            Anim.FadeIn(_sysMenu);
        }

        private ContextMenu BuildSystemMenu()
        {
            var menu = new ContextMenu();

            // E923/E921/E922/E8BB are the glyphs the caption buttons draw, so the menu and the
            // buttons agree. E7C2 is the four-way move arrow, E740 the diagonal resize arrow.
            void Add(string key, string fallback, int cmd, int glyph, string gesture = "", bool danger = false)
            {
                var mi = new MenuItem { Tag = cmd, InputGestureText = gesture };
                mi.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
                // A locale without the key would otherwise show the key name.
                if (Loc(key) == key) mi.Header = fallback;

                var icon = new Glyph { Text = char.ConvertFromUtf32(glyph) };
                icon.SetResourceReference(StyleProperty, "MenuIcon");
                if (danger) icon.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed");
                mi.Icon = icon;

                mi.Click += (_, _) => SendSysCommand(cmd);
                menu.Items.Add(mi);
            }

            Add("Str_Sys_Restore",  "Restore",  SC_RESTORE,  0xE923);
            Add("Str_Sys_Move",     "Move",     SC_MOVE,     0xE7C2);
            Add("Str_Sys_Size",     "Size",     SC_SIZE,     0xE740);
            Add("Str_Sys_Minimize", "Minimize", SC_MINIMIZE, 0xE921);
            Add("Str_Sys_Maximize", "Maximize", SC_MAXIMIZE, 0xE922);
            menu.Items.Add(new Separator());
            Add("Str_Sys_Close",    "Close",    SC_CLOSE,    0xE8BB, gesture: "Alt+F4", danger: true);
            return menu;
        }

        private void SendSysCommand(int cmd)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            // Posted, not sent: the menu is still closing, and SC_MOVE and SC_SIZE start a modal
            // loop that must not run inside the click handler.
            PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(cmd), IntPtr.Zero);   // TabHandoff.cs
        }
    }
}
