using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace KillerShell.Services
{
    /// <summary>
    /// The shell's own data object for real files and folders, the same one Explorer hands to
    /// a drag. Used for every drag that leaves KillerShell.
    /// </summary>
    /// <remarks>
    /// NativeDataObject carried a bare CF_HDROP and plain text, and it was emptied the moment
    /// DoDragDrop returned. Explorer, GIMP and Telegram read the file list during the drop and
    /// never noticed. Microsoft Teams did: it rejected every file with "missing necessary data".
    /// The shell's object carries the full set of shell formats (the item ID list, CF_HDROP,
    /// FileNameW and the rest), answers SetData so the drag image still attaches, and stays
    /// alive for as long as a drop target holds a reference to it.
    ///
    /// The items are passed as absolute ID lists under the desktop, so one drag can carry
    /// search results from many folders, exactly as an Explorer search window does.
    /// </remarks>
    internal static class ShellDataObject
    {
        /// <summary>
        /// Builds the shell data object for <paramref name="paths"/>, or null when any path
        /// cannot be resolved to a shell item. The caller owns the returned RCW and must release
        /// it with Marshal.FinalReleaseComObject after DoDragDrop returns, which is what takes
        /// the shell's drag-image window down.
        /// </summary>
        internal static ComDataObject? Create(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return null;

            var pidls = new IntPtr[paths.Count];
            IntPtr desktop = IntPtr.Zero;
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    int hr = SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _);
                    if (hr < 0 || pidls[i] == IntPtr.Zero) return null;
                }

                if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DESKTOP, out desktop) < 0
                    || desktop == IntPtr.Zero)
                    return null;

                int hrCreate = CIDLData_CreateFromIDArray(desktop, (uint)pidls.Length, pidls, out ComDataObject data);
                return hrCreate < 0 ? null : data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DragDiag] ShellDataObject.Create threw: {ex}");
                return null;
            }
            finally
            {
                // The data object copies every ID list it is given, so ours are freed here
                // whether or not it was built.
                foreach (var p in pidls) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
                if (desktop != IntPtr.Zero) Marshal.FreeCoTaskMem(desktop);
            }
        }

        private const int CSIDL_DESKTOP = 0x0000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bindingContext,
                                                     out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern int CIDLData_CreateFromIDArray(IntPtr pidlFolder, uint cidl,
                                                             [In] IntPtr[] apidl,
                                                             [MarshalAs(UnmanagedType.Interface)] out ComDataObject dataObject);
    }
}
