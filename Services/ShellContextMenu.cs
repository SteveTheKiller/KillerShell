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
            using var session = CreateSession();
            return Show(new WindowInteropHelper(owner).Handle, paths, session);
        }

        /// <summary>The native boundary for a single menu, owned and disposed by the caller.</summary>
        public interface IShellMenuSession : IDisposable
        {
            bool TryInitialize(string firstPath);
            bool TryAddPath(string path);
            bool Show(IntPtr owner);
        }

        /// <summary>Creates a Windows selection session without opening a menu.</summary>
        public static IShellMenuSession CreateSession() => new NativeSession();

        /// <summary>Whether one native folder can represent the entire selection.</summary>
        public static bool SupportsSelection(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;
            try
            {
                string? parent = Path.GetDirectoryName(paths[0]);
                // A root has a shell namespace parent even though it has no filesystem parent.
                if (string.IsNullOrEmpty(parent) && paths.Length != 1) return false;
                foreach (string path in paths)
                    if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase))
                        return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Builds one complete selection before asking Windows to display its menu.</summary>
        public static bool Show(IntPtr owner, string[] paths, IShellMenuSession session)
        {
            if (owner == IntPtr.Zero || !SupportsSelection(paths)) return false;
            try
            {
                if (!session.TryInitialize(paths[0])) return false;
                foreach (string path in paths)
                    if (!session.TryAddPath(path)) return false;
                return session.Show(owner);
            }
            catch { return false; }
        }

        private sealed class NativeSession : IShellMenuSession
        {
            private IShellFolder? _folder;
            private IContextMenu? _menu;
            private IntPtr _parentPidl;
            private IntPtr _firstChild;
            private string? _firstPath;
            private IntPtr _hMenu;
            private readonly List<IntPtr> _children = new();

            public bool TryInitialize(string firstPath)
            {
                if (SHParseDisplayName(firstPath, IntPtr.Zero, out _parentPidl, 0, out _) != 0
                    || _parentPidl == IntPtr.Zero)
                    return false;
                if (SHBindToParent(_parentPidl, ref IID_IShellFolder, out _folder, out _firstChild) != 0)
                    return false;
                _firstPath = firstPath;
                return _folder != null && _firstChild != IntPtr.Zero;
            }

            public bool TryAddPath(string path)
            {
                if (_folder == null) return false;
                if (string.Equals(path, _firstPath, StringComparison.OrdinalIgnoreCase))
                {
                    IntPtr first = ILClone(_firstChild);
                    if (first == IntPtr.Zero) return false;
                    _children.Add(first);
                    return true;
                }
                uint eaten = 0, attrs = 0;
                int result = _folder.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, Path.GetFileName(path),
                    ref eaten, out IntPtr child, ref attrs);
                if (child != IntPtr.Zero) _children.Add(child);
                return result == 0 && child != IntPtr.Zero;
            }

            public bool Show(IntPtr owner)
            {
                if (_folder == null || _children.Count == 0) return false;
                if (_folder.GetUIObjectOf(owner, (uint)_children.Count, _children.ToArray(),
                    IID_IContextMenu, IntPtr.Zero, out object cm) != 0) return false;
                _menu = cm as IContextMenu;
                if (_menu == null) return false;
                _hMenu = CreatePopupMenu();
                if (_hMenu == IntPtr.Zero) return false;
                _menu.QueryContextMenu(_hMenu, 0, CmdFirst, CmdLast, CMF_EXPLORE);
                if (GetMenuItemCount(_hMenu) <= 0) return false;
                SetForegroundWindow(owner);
                GetCursorPos(out POINT pt);
                int chosen = TrackPopupMenuEx(_hMenu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
                    pt.X, pt.Y, owner, IntPtr.Zero);
                if (chosen <= 0) return true;
                var info = new CMINVOKECOMMANDINFO
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    hwnd = owner,
                    lpVerb = (IntPtr)(chosen - CmdFirst),
                    nShow = SW_SHOWNORMAL,
                };
                _menu.InvokeCommand(ref info);
                return true;
            }

            public void Dispose()
            {
                if (_hMenu != IntPtr.Zero) DestroyMenu(_hMenu);
                foreach (var child in _children) Marshal.FreeCoTaskMem(child);
                if (_parentPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(_parentPidl);
                if (_menu != null) Marshal.ReleaseComObject(_menu);
                if (_folder != null) Marshal.ReleaseComObject(_folder);
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

        private static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
        private static Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

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
        private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out IntPtr child);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string path, IntPtr bindingContext,
            out IntPtr pidl, uint attributes, out uint resultAttributes);

        [DllImport("shell32.dll")]
        private static extern IntPtr ILClone(IntPtr pidl);

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
