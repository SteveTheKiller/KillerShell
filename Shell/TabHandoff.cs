using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using KillerShell.Models;
using KillerShell.Terminal;

// Dragging a tab straight into ANOTHER KillerShell window - the reverse of tearing one out
// (TabTearOut.cs). Two windows are two OS processes (NewWindow.cs), so a drop on another
// window's frame cannot literally move the tab's live control across the boundary any more than
// tear-out can: what crosses is the same --cwd/--shell/--edit/--processes/--eventviewer/
// --performance/--registry relaunch state
// TabTearOut.BuildRelaunchArgs already builds, carried over as a WM_COPYDATA message instead of
// a process command line. The receiving window opens it as one MORE tab rather than replacing
// what it already has - ApplyStartupTearOut (TabTearOut.cs) is for a window that exists to BE
// that one tab; a window on the other end of a merge already has a life of its own.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // ═══════════════════════════════════════════════════════════
        //  FINDING THE OTHER WINDOW
        // ═══════════════════════════════════════════════════════════
        // POINT is Chrome.cs's own (WmGetMinMaxInfo) - one struct per native shape per window,
        // not one per file that happens to need it.
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);

        // Used by FindElevatedKillerShellWindow (Elevation.cs) as well as the point-hit search
        // below - one set of P/Invokes for "is this HWND one of ours" and "what does its title
        // bar say", regardless of which caller needs them. A private member declared in one file
        // of a partial class is visible from every other file of the same class, so Elevation.cs
        // needs no declarations of its own.
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint GA_ROOT = 2;

        /// <summary>
        /// True when <paramref name="hwnd"/> belongs to another KillerShell.exe process - same
        /// exe path as this one, whichever process actually owns the window. Shared by the
        /// point-hit search below and by FindElevatedKillerShellWindow (Elevation.cs).
        /// </summary>
        private static bool IsKillerShellProcessWindow(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                string? exe  = proc.MainModule?.FileName;
                string? mine = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exe == null || mine == null) return false;
                return string.Equals(exe, mine, StringComparison.OrdinalIgnoreCase);
            }
            // A process we cannot open (elevated and we are not, or it exited mid-check) is not
            // a usable target either way - same result as "nothing there".
            catch { return false; }
        }

        /// <summary>
        /// The top-level HWND of another, DIFFERENT KillerShell process sitting under
        /// <paramref name="screenPoint"/>, or <see cref="IntPtr.Zero"/> when there is none -
        /// nothing there, some other app entirely, or this very window (a drag that wandered
        /// back over itself is a reorder, not a merge).
        /// </summary>
        private IntPtr FindOtherKillerShellWindowAt(Point screenPoint)
        {
            var hit = WindowFromPoint(new POINT { x = (int)screenPoint.X, y = (int)screenPoint.Y });
            if (hit == IntPtr.Zero) return IntPtr.Zero;

            var root = GetAncestor(hit, GA_ROOT);
            if (root == IntPtr.Zero) root = hit;

            var self = new WindowInteropHelper(this).Handle;
            if (root == self) return IntPtr.Zero;

            return IsKillerShellProcessWindow(root) ? root : IntPtr.Zero;
        }

        // ═══════════════════════════════════════════════════════════
        //  HOVER FEEDBACK
        //  The accent caret that shows where a cross-window drop will land - the same
        //  TabDropCaret an in-process cross-PANE drag already lights up (PaneDrag.cs), lit here
        //  by the window being dragged OVER rather than the one doing the dragging.
        // ═══════════════════════════════════════════════════════════
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string msg);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Registered once per process by its string, so every KillerShell instance - whichever
        // one asks first - ends up with the same message ID; nothing to coordinate.
        private static readonly uint WM_KS_TABHOVER = RegisterWindowMessage("KillerShell_TabHover");

        // wParam is 1 for "hover here" / 0 for "gone" (lParam then unused). Packed rather than
        // sent as WM_COPYDATA: this fires on every mouse-move while a drag is outside the
        // window, and a synchronous SendMessage that often would make the drag feel exactly as
        // laggy as whatever the target window happens to be doing at that instant. PostMessage
        // just queues it and moves on.
        private static IntPtr PackPoint(Point p)
            => new(((int)p.X & 0xFFFF) | (((int)p.Y & 0xFFFF) << 16));

        private static Point UnpackPoint(IntPtr lParam)
        {
            long l = lParam.ToInt64();
            short x = unchecked((short)(l & 0xFFFF));
            short y = unchecked((short)((l >> 16) & 0xFFFF));
            return new Point(x, y);
        }

        // Which window (if any) is currently showing OUR drag's caret - tracked so a move that
        // leaves it, or a drag that ends without dropping there, can tell it to stop.
        private IntPtr _hoverTargetHwnd = IntPtr.Zero;

        /// <summary>
        /// Called on every drag move once the pointer clears this window's own frame
        /// (Tab_DragMove, Tabs.cs). Tells whichever OTHER KillerShell window is under the
        /// pointer to light up its drop caret, and tells the PREVIOUS one (if different) to turn
        /// its back off.
        /// </summary>
        private void UpdateCrossWindowHover(System.Windows.Input.MouseEventArgs e)
        {
            if (!OutsideWindow(e)) { ClearCrossWindowHover(); return; }   // TabTearOut.cs

            var screenPt = PointToScreen(e.GetPosition(this));
            var target = FindOtherKillerShellWindowAt(screenPt);

            if (target != _hoverTargetHwnd)
            {
                if (_hoverTargetHwnd != IntPtr.Zero)
                    PostMessage(_hoverTargetHwnd, WM_KS_TABHOVER, IntPtr.Zero, IntPtr.Zero);
                _hoverTargetHwnd = target;
            }

            if (target != IntPtr.Zero)
                PostMessage(target, WM_KS_TABHOVER, new IntPtr(1), PackPoint(screenPt));
        }

        /// <summary>Tell the last-hovered window (if any) to drop its caret. Safe to call when
        /// nothing is being hovered - the common case, every ordinary drag end.</summary>
        private void ClearCrossWindowHover()
        {
            if (_hoverTargetHwnd == IntPtr.Zero) return;
            PostMessage(_hoverTargetHwnd, WM_KS_TABHOVER, IntPtr.Zero, IntPtr.Zero);
            _hoverTargetHwnd = IntPtr.Zero;
        }

        /// <summary>Called from Chrome.cs's WndProc on WM_KS_TABHOVER.</summary>
        private void HandleTabHover(IntPtr wParam, IntPtr lParam)
        {
            if (wParam == IntPtr.Zero) { HideIncomingDropCaret(); return; }
            ShowIncomingDropCaret(UnpackPoint(lParam));
        }

        /// <summary>
        /// Lights up this window's OWN TabDropCaret at the point another window's drag is
        /// currently over, translated from screen coordinates into the focused pane's own strip.
        /// No ghost - there is nothing of the dragged tab to show, only where it would land.
        /// </summary>
        private void ShowIncomingDropCaret(Point screenPt)
        {
            var target = Pane;   // Panes.cs - this window's own focused pane
            var stripPt = target.TabStrip.PointFromScreen(screenPt);

            bool onStrip = target.TabBar.Visibility == Visibility.Visible
                           && stripPt.Y >= 0 && stripPt.Y <= target.TabBar.ActualHeight;
            if (!onStrip) { HideIncomingDropCaret(); return; }

            TabDragGhost.Visibility = Visibility.Collapsed;   // caret only - no ghost to show
            DragLayer.Visibility = Visibility.Visible;
            PositionDropCaret(target, stripPt);               // PaneDrag.cs
        }

        private void HideIncomingDropCaret()
        {
            TabDropCaret.Visibility = Visibility.Collapsed;
            DragLayer.Visibility = Visibility.Collapsed;
            TabDragGhost.Visibility = Visibility.Visible;   // restore default for this window's own drags
        }

        // ═══════════════════════════════════════════════════════════
        //  SENDING
        // ═══════════════════════════════════════════════════════════
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int    cbData;
            public IntPtr lpData;
        }

        // COPYDATASTRUCT.dwData is a free-form tag; only one kind of handoff exists today, so
        // there is nothing to dispatch on beyond confirming a message really is one of ours.
        private const int HandoffTag = 0x4B534854;   // "KSHT"

        /// <summary>
        /// Hand <paramref name="t"/> to the window at <paramref name="targetHwnd"/> and close it
        /// here - the merge half of a drag that let go over another KillerShell window
        /// (Tab_DragUp, Tabs.cs).
        /// </summary>
        private void MergeTabIntoWindow(SearchTab t, IntPtr targetHwnd)
        {
            string? args = BuildRelaunchArgs(t, out _);   // TabTearOut.cs
            if (args == null) return;   // needs a save first - BuildRelaunchArgs already set the status

            SendHandoffArgs(args, targetHwnd);

            // Same rule TearOutTab follows: only close it here once the handoff is actually on
            // its way, and never leave a pane with zero tabs behind - CloseTab would just spawn
            // a fresh blank one in its place (Tabs.cs), which makes "move my only tab" read as
            // "clone this window" instead of moving it.
            if (_tabs.Count > 1) CloseTab(t);   // Tabs.cs
        }

        /// <summary>
        /// Sends a relaunch-args flags string to another KillerShell window over WM_COPYDATA and
        /// brings it to the foreground - the ONE place this app builds and sends that message.
        /// Used by MergeTabIntoWindow above (a dragged tab) and by RelaunchElevatedProcesses /
        /// RelaunchElevatedEventViewer (Elevation.cs, handing an admin tab request to an
        /// already-open elevated window instead of prompting UAC for a new one). Both land on the
        /// receiving end in the exact same place, ApplyHandoff below, so a window on the other
        /// side of either call cannot tell which one sent it.
        /// </summary>
        private static void SendHandoffArgs(string args, IntPtr targetHwnd)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(args + "\0");
            var buf = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buf, bytes.Length);
                var cds = new COPYDATASTRUCT
                {
                    dwData = new IntPtr(HandoffTag),
                    cbData = bytes.Length,
                    lpData = buf,
                };
                SendMessage(targetHwnd, WM_COPYDATA, IntPtr.Zero, ref cds);   // Chrome.cs - WM_COPYDATA
            }
            finally { Marshal.FreeHGlobal(buf); }

            if (IsWindow(targetHwnd)) SetForegroundWindow(targetHwnd);
        }

        // ═══════════════════════════════════════════════════════════
        //  RECEIVING
        // ═══════════════════════════════════════════════════════════
        /// <summary>Called from Chrome.cs's WndProc on WM_COPYDATA.</summary>
        private IntPtr HandleCopyData(IntPtr lParam)
        {
            try
            {
                var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT))!;
                if (cds.dwData.ToInt64() != HandoffTag || cds.lpData == IntPtr.Zero) return IntPtr.Zero;

                string args = Marshal.PtrToStringUni(cds.lpData) ?? string.Empty;
                HideIncomingDropCaret();   // the drop landed - stop showing where it MIGHT go
                // Off the message pump before touching tabs/panes - WM_COPYDATA is delivered
                // synchronously and SendMessage on the OTHER end is blocked waiting for us.
                Dispatcher.BeginInvoke(new Action(() => ApplyHandoff(args)));
            }
            catch { /* malformed payload - ignore rather than crash on someone else's message */ }
            return new IntPtr(1);
        }

        /// <summary>
        /// Opens the handed-off tab as one MORE tab in the focused pane. Unlike
        /// ApplyStartupTearOut (TabTearOut.cs), which clears the window's seed tab because that
        /// window exists to BE the one thing it was launched with, this window already has a
        /// life of its own - a merge lands beside what is already open, not instead of it.
        /// </summary>
        private void ApplyHandoff(string args)
        {
            var tok = TokenizeArgs(args);   // TabTearOut.cs

            if (tok.Exists(a => string.Equals(a, "--processes", StringComparison.OrdinalIgnoreCase)))
            {
                OpenTaskManager();   // ProcessTabs.cs - singleton; fine even as an extra tab
            }
            else if (tok.Exists(a => string.Equals(a, "--eventviewer", StringComparison.OrdinalIgnoreCase)))
            {
                // Only ever arrives here already elevated: RelaunchElevatedEventViewer only
                // hands off to a window FindElevatedKillerShellWindow (Elevation.cs) already
                // confirmed is elevated, the same way ApplyStartupTearOut's own --eventviewer
                // branch (TabTearOut.cs) only ever runs in a process that was launched elevated.
                OpenEventViewer();   // EventViewerTabs.cs - singleton; fine even as an extra tab
            }
            else if (tok.Exists(a => string.Equals(a, "--performance", StringComparison.OrdinalIgnoreCase)))
            {
                OpenPerformanceMonitor();   // PerformanceTabs.cs - singleton; fine even as an extra tab
            }
            else if (tok.Exists(a => string.Equals(a, "--storage", StringComparison.OrdinalIgnoreCase)))
            {
                OpenStorageAnalyzer();   // StorageTabs.cs - singleton; fine even as an extra tab
            }
            else if (tok.Exists(a => string.Equals(a, "--registry", StringComparison.OrdinalIgnoreCase)))
            {
                // Only ever arrives here already elevated: RelaunchElevatedRegistryEditor only
                // hands off to a window FindElevatedKillerShellWindow (Elevation.cs) already
                // confirmed is elevated, the same way --eventviewer above does.
                OpenRegistryEditor();   // RegistryEditorTabs.cs - singleton; fine even as an extra tab
            }
            else
            {
                int edit  = tok.FindIndex(a => string.Equals(a, "--edit", StringComparison.OrdinalIgnoreCase));
                int shell = tok.FindIndex(a => string.Equals(a, "--shell", StringComparison.OrdinalIgnoreCase));
                int cwdI  = tok.FindIndex(a => string.Equals(a, "--cwd", StringComparison.OrdinalIgnoreCase));
                string cwd = cwdI >= 0 && cwdI + 1 < tok.Count ? tok[cwdI + 1] : HomeFolder;

                if (edit >= 0 && edit + 1 < tok.Count && System.IO.File.Exists(tok[edit + 1]))
                    OpenForEditing(tok[edit + 1]);   // EditorTabs.cs
                else if (shell >= 0 && shell + 1 < tok.Count)
                    OpenShell(string.Equals(tok[shell + 1], "cmd", StringComparison.OrdinalIgnoreCase)
                              ? TerminalProfile.Cmd() : TerminalProfile.PowerShell(), cwd);
                else
                    OpenFolderTabLeft(cwd);   // TerminalTabs.cs
            }

            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }
}
