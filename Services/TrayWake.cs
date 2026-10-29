#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KillerShell.Services
{
    /// <summary>
    /// Brings back a KillerShell that is hidden in the notification area when the user starts the
    /// app again from the taskbar or Start. With Close to system tray on (About card), closing the
    /// window hides it, and before this every launch was a new process, so the icon on the
    /// taskbar opened a second KillerShell while the first sat in the tray with its tabs.
    ///
    /// KillerShell has no single-instance channel: Ctrl+N, Explorer's folder verbs, torn-out
    /// documents and elevated windows are all processes of their own and stay that way. Only a
    /// launch with no arguments at all looks for a hidden instance. It finds the tray's own
    /// message window (CloseToTray names it "&lt;App&gt;TrayIcon"), checks that its process has no
    /// window showing, and delivers the icon's click, which that process answers with
    /// CloseToTray.ShowWindow. An instance that is hung, or elevated where this process is not, does
    /// not answer, and the launch carries on as a normal start so the user is never left with
    /// nothing on screen.
    /// </summary>
    internal static class TrayWake
    {
        // CloseToTray's callback message (WM_APP + 0x51) and the event it reads as a click.
        private const int CallbackMessage = 0x8000 + 0x51;
        private const int WM_LBUTTONUP = 0x0202;
        private const uint SMTO_BLOCK = 0x0001, SMTO_ABORTIFHUNG = 0x0002;

        /// <summary>A launch with no arguments: the taskbar or Start icon, or the exe double-clicked.
        /// Anything else is a job of its own and opens its own window.</summary>
        internal static bool IsPlainLaunch(string[] args) => args.Length == 0;

        /// <summary>The tray window of the first other process that has no visible window, or Zero.
        /// This process is skipped: it has no main window yet, so it would look hidden itself.</summary>
        internal static IntPtr PickHidden(IEnumerable<(IntPtr Handle, int Pid)> trayWindows, int ownPid,
            Func<int, bool> processHasVisibleWindow)
        {
            foreach (var (handle, pid) in trayWindows)
            {
                if (pid == 0 || pid == ownPid) continue;
                if (!processHasVisibleWindow(pid)) return handle;
            }
            return IntPtr.Zero;
        }

        /// <summary>True when a hidden instance was told to show its window, and this process
        /// should exit without opening one.</summary>
        internal static bool TryWakeHiddenInstance()
        {
            try
            {
                var trays = FindWindowsTitled(AppInfo.DisplayName + "TrayIcon");
                IntPtr target = PickHidden(trays, Process.GetCurrentProcess().Id, HasVisibleWindow);
                if (target == IntPtr.Zero) return false;
                // The user clicked our icon, so this process may hand the foreground to the one
                // that is actually going to show a window.
                GetWindowThreadProcessId(target, out uint pid);
                AllowSetForegroundWindow(pid);
                IntPtr delivered = SendMessageTimeout(target, CallbackMessage, IntPtr.Zero, new IntPtr(WM_LBUTTONUP),
                    SMTO_BLOCK | SMTO_ABORTIFHUNG, 2000, out _);
                return delivered != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private static List<(IntPtr Handle, int Pid)> FindWindowsTitled(string title)
        {
            var found = new List<(IntPtr, int)>();
            var text = new StringBuilder(title.Length + 2);
            EnumWindows((hwnd, _) =>
            {
                text.Clear();
                int length = GetWindowText(hwnd, text, text.Capacity);
                if (length == title.Length && text.ToString() == title)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    found.Add((hwnd, (int)pid));
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool HasVisibleWindow(int pid)
        {
            bool visible = false;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if ((int)owner == pid && IsWindowVisible(hwnd)) { visible = true; return false; }
                return true;
            }, IntPtr.Zero);
            return visible;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);
    }
}
