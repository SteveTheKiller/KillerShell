using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KillerShell.Services
{
    // The Type column's text, asked of the shell so it reads exactly as Explorer's does and
    // arrives already in the Windows display language.
    //
    // Cached per EXTENSION, the same bargain IconCache makes: a listing of 100k files is a
    // handful of shell calls rather than 100k. SHGFI_USEFILEATTRIBUTES answers from the
    // extension alone without touching the file, which is also what lets a virtual archive path
    // or a demo-mode path that exists nowhere on disk get a real answer.
    public static class ShellTypeNames
    {
        private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

        private const string FolderKey = "<dir>";

        public static string For(string path, bool isDirectory)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            // A drive root is the one directory whose type is not "File folder", and the shell
            // only knows which kind of disk it is by looking at it.
            bool isRoot = isDirectory && path.Length <= 3 && path.Length >= 2 && path[1] == ':';

            string key = isRoot ? path : isDirectory ? FolderKey : ExtensionOf(path);

            lock (Cache)
                if (Cache.TryGetValue(key, out var hit)) return hit;

            string name = isRoot ? Query(path, FILE_ATTRIBUTE_DIRECTORY, real: true)
                        : isDirectory ? Query("folder", FILE_ATTRIBUTE_DIRECTORY, real: false)
                        : Query(key.Length == 0 ? "file" : "file" + key, FILE_ATTRIBUTE_NORMAL, real: false);

            lock (Cache) Cache[key] = name;
            return name;
        }

        // Path.GetExtension throws on the characters a virtual archive path can carry, and all
        // that is wanted is what follows the last dot of the last segment.
        private static string ExtensionOf(string path)
        {
            int dot = path.LastIndexOf('.');
            if (dot < 0 || dot == path.Length - 1) return string.Empty;
            int sep = path.LastIndexOfAny(['\\', '/']);
            return dot > sep ? path[dot..] : string.Empty;
        }

        private static string Query(string pathOrName, uint attributes, bool real)
        {
            try
            {
                var info = new SHFILEINFO();
                uint flags = SHGFI_TYPENAME;
                if (!real) flags |= SHGFI_USEFILEATTRIBUTES;

                IntPtr r = SHGetFileInfo(pathOrName, attributes, ref info,
                                         (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                return r == IntPtr.Zero ? string.Empty : info.szTypeName ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private const uint SHGFI_TYPENAME          = 0x400;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL    = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int    iIcon;
            public uint   dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    }
}
