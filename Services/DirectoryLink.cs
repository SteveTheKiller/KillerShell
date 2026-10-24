using System;
using System.IO;
using System.Runtime.InteropServices;

namespace KillerShell.Services
{
    /// <summary>
    /// Whether a folder is a LINK - a junction, a directory symlink, or any other reparse point
    /// whose tag says it names somewhere else - as opposed to a folder that merely carries a
    /// reparse point. The attribute alone cannot tell them apart: dedup, WOF-compressed,
    /// ProjFS (VFS for Git) and container-layer folders set FILE_ATTRIBUTE_REPARSE_POINT too,
    /// and treating those as links made copy skip them, cross-drive move refuse them, and tar
    /// refuse a destination under them. Windows marks the link kinds with the name-surrogate
    /// bit in the tag (IsReparseTagNameSurrogate), which is what this reads.
    /// </summary>
    internal static class DirectoryLink
    {
        private const uint NameSurrogateBit = 0x20000000;
        internal const uint TagMountPoint = 0xA0000003;
        internal const uint TagSymlink = 0xA000000C;

        /// <summary>IsReparseTagNameSurrogate, from winnt.h.</summary>
        internal static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;

        /// <summary>
        /// True for a junction or symlink at <paramref name="path"/>. Unreadable paths answer
        /// false, as the attribute test they replace did; when the tag itself cannot be read
        /// the answer is true, so an unknown reparse point is still treated as the riskier kind.
        /// </summary>
        internal static bool IsLink(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch { return false; }
            return IsLink(path, attributes);
        }

        /// <summary>As <see cref="IsLink(string)"/>, for a caller that already read the attributes.</summary>
        internal static bool IsLink(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
            uint? tag = ReadTag(path);
            return tag == null || IsNameSurrogate(tag.Value);
        }

        /// <summary>
        /// The reparse tag, from FindFirstFile's WIN32_FIND_DATA.dwReserved0, which Windows fills
        /// with the tag whenever the entry carries FILE_ATTRIBUTE_REPARSE_POINT. Null when the
        /// entry cannot be found or names a volume root, which has no parent to list it.
        /// </summary>
        internal static uint? ReadTag(string path)
        {
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 0 || trimmed.EndsWith(":", StringComparison.Ordinal)) return null;
            IntPtr find = FindFirstFileW(trimmed, out var data);
            if (find == InvalidHandle) return null;
            FindClose(find);
            return (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : (uint?)null;
        }

        private static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out Win32FindData data);

        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr find);
    }
}
