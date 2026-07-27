using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KillerShell.Services
{
    // The real Explorer context menu for a set of files, plus the real properties dialog.
    //
    // Everything a shell extension adds - 7-Zip, TortoiseGit, an AV scanner, Send To, the full
    // Open With list - lives behind IContextMenu, and there is no managed route to it. So: bind
    // the containing folder, get a child PIDL per file, ask the folder for an IContextMenu over
    // them, let it fill an HMENU, show that with TrackPopupMenuEx, and hand the chosen command
    // back to the same IContextMenu to invoke.
    //
    // The menu that appears is a native Win32 menu, themed by Windows rather than by KillerShell.
    // That is the trade rather than an oversight: the entire value here is that it is genuinely
    // the shell's menu, and redrawing it in themed WPF would mean reimplementing the rendering of
    // every extension on the machine, which cannot be done.
    public static class ShellContextMenu
    {
        private const uint CmdFirst = 1;
        private const uint CmdLast  = 0x7FFF;

        /// <summary>
        /// Pops the shell menu for these paths at the cursor position. Returns false if the menu
        /// could not be built or shown, so the caller can say so instead of appearing to ignore
        /// the click.
        /// </summary>
        public static bool Show(Window owner, string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;

            IntPtr hwnd = new WindowInteropHelper(owner).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // One IContextMenu covers one folder, so take the clicked file's folder and only the
            // paths that share it. A results list spans the whole tree, so a mixed selection
            // silently narrowing here is better than refusing to open the menu at all.
            string? dir = Path.GetDirectoryName(paths[0]);
            if (string.IsNullOrEmpty(dir)) return false;

            var same = new List<string>();
            foreach (var p in paths)
                if (string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase))
                    same.Add(p);
            if (same.Count == 0) return false;

            IShellFolder? desktop = null;
            IShellFolder? folder  = null;
            IContextMenu? menu    = null;
            IntPtr dirPidl = IntPtr.Zero;
            IntPtr hMenu   = IntPtr.Zero;
            var children   = new List<IntPtr>();

            try
            {
                if (SHGetDesktopFolder(out desktop) != 0 || desktop == null) return false;

                uint eaten = 0, attrs = 0;
                if (desktop.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, dir!, ref eaten, out dirPidl, ref attrs) != 0)
                    return false;

                if (desktop.BindToObject(dirPidl, IntPtr.Zero, IID_IShellFolder, out object bound) != 0)
                    return false;
                folder = bound as IShellFolder;
                if (folder == null) return false;

                foreach (var p in same)
                {
                    eaten = 0; attrs = 0;
                    if (folder.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, Path.GetFileName(p),
                                                ref eaten, out IntPtr child, ref attrs) == 0)
                        children.Add(child);
                }
                if (children.Count == 0) return false;

                var arr = children.ToArray();
                if (folder.GetUIObjectOf(hwnd, (uint)arr.Length, arr, IID_IContextMenu, IntPtr.Zero, out object cm) != 0)
                    return false;
                menu = cm as IContextMenu;
                if (menu == null) return false;

                hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return false;
                menu.QueryContextMenu(hMenu, 0, CmdFirst, CmdLast, CMF_EXPLORE);

                // An empty menu means the shell gave us nothing to show - report it rather than
                // flashing an empty popup.
                if (GetMenuItemCount(hMenu) <= 0) return false;

                // TrackPopupMenuEx dismisses itself the moment the owning window is not in the
                // foreground, which is the documented reason menus "flash and vanish".
                SetForegroundWindow(hwnd);

                GetCursorPos(out POINT pt);
                int chosen = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
                                              pt.X, pt.Y, hwnd, IntPtr.Zero);

                // Zero means dismissed without choosing, which is a normal outcome, not a failure.
                if (chosen <= 0) return true;

                // The verb is the command's offset from CmdFirst, passed as a resource-style
                // integer rather than a string pointer.
                var info = new CMINVOKECOMMANDINFO
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    hwnd   = hwnd,
                    lpVerb = (IntPtr)(chosen - CmdFirst),
                    nShow  = SW_SHOWNORMAL,
                };
                menu.InvokeCommand(ref info);
                return true;
            }
            catch { return false; }   // a misbehaving shell extension is not ours to fix
            finally
            {
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
                foreach (var c in children) Marshal.FreeCoTaskMem(c);
                if (dirPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(dirPidl);
                if (menu != null)    Marshal.ReleaseComObject(menu);
                if (folder != null)  Marshal.ReleaseComObject(folder);
                if (desktop != null) Marshal.ReleaseComObject(desktop);
            }
        }

        /// <summary>
        /// The standard Windows properties dialog. Returns false if the shell refused, so the
        /// caller can report it rather than looking like the click was ignored.
        /// </summary>
        public static bool ShowProperties(string path)
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                // INVOKEIDLIST is what makes the properties verb work at all - it tells the shell
                // to resolve the item's PIDL and use its full verb set rather than the plain
                // file-association ones. NOCLOSEPROCESS is deliberately NOT set: nothing here
                // waits on or closes a process, and leaving it on just leaks the handle it
                // returns. ASYNCOK lets the shell open the dialog on its own thread, which is
                // what stops the call blocking our UI thread while the dialog lives.
                fMask  = SEE_MASK_INVOKEIDLIST | SEE_MASK_ASYNCOK,
                lpVerb = "properties",
                lpFile = path,
                nShow  = SW_SHOWNORMAL,
            };

            try { return ShellExecuteEx(ref info); }
            catch { return false; }
        }

        // ── Interop ──────────────────────────────────────────────
        private const uint CMF_EXPLORE     = 0x00000004;
        private const uint TPM_RETURNCMD   = 0x0100;
        private const uint TPM_LEFTALIGN   = 0x0000;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const int  SW_SHOWNORMAL   = 1;

        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        private const uint SEE_MASK_ASYNCOK      = 0x00100000;

        private static Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        private static Guid IID_IContextMenu = new Guid("000214E4-0000-0000-C000-000000000046");

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CMINVOKECOMMANDINFO
        {
            public int    cbSize;
            public uint   fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int    nShow;
            public uint   dwHotKey;
            public IntPtr hIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int    cbSize;
            public uint   fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
            public int    nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
            public IntPtr hkeyClass;
            public uint   dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
                [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig] int GetAttributesOf(uint cidl,
                [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
            [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
                [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid,
                IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
                [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu,
                uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pwReserved,
                IntPtr pszName, uint cchMax);
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] private static extern bool   DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] private static extern bool   GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern int    GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll")] private static extern bool   SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y,
            IntPtr hwnd, IntPtr lptpm);
    }
}
