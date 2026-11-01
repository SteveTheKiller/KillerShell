using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace KillerShell.Services
{
    internal readonly struct StorageDirectoryEntry
    {
        internal string Name { get; }
        internal long Size { get; }
        internal bool IsDirectory { get; }
        /// <summary>A file that carries a reparse tag (a link, a cloud placeholder). Its size is the
        /// one the directory reports. Reparse directories never appear as entries.</summary>
        internal bool IsReparsePoint { get; }

        internal StorageDirectoryEntry(string name, long size, bool isDirectory, bool isReparsePoint = false)
        {
            Name = name;
            Size = size;
            IsDirectory = isDirectory;
            IsReparsePoint = isReparsePoint;
        }
    }

    internal readonly struct StorageDirectoryReadResult
    {
        internal IReadOnlyList<StorageDirectoryEntry> Entries { get; }
        internal bool Skipped { get; }
        /// <summary>Directory links (junctions and symbolic links) left out of this folder's entries.</summary>
        internal int SkippedReparsePoints { get; }

        internal StorageDirectoryReadResult(IReadOnlyList<StorageDirectoryEntry> entries, bool skipped, int skippedReparsePoints = 0)
        {
            Entries = entries;
            Skipped = skipped;
            SkippedReparsePoints = skippedReparsePoints;
        }
    }

    internal static class StorageDirectoryReader
    {
        /// <summary>The read the storage map uses. It leaves linked files out as well as linked
        /// folders, so the map's totals match its volume-index path, which skips every reparse entry.</summary>
        internal static StorageDirectoryReadResult ReadForMap(string path, CancellationToken cancellationToken)
        {
            var read = Read(path, cancellationToken);
            var kept = new List<StorageDirectoryEntry>(read.Entries.Count);
            foreach (var entry in read.Entries)
                if (!entry.IsReparsePoint) kept.Add(entry);
            return new StorageDirectoryReadResult(kept, read.Skipped, read.SkippedReparsePoints);
        }

        internal static StorageDirectoryReadResult Read(string path, CancellationToken cancellationToken)
        {
            string extendedPath = path;
            if (!extendedPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                extendedPath = Path.GetFullPath(extendedPath);
                extendedPath = extendedPath.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + extendedPath.Substring(2)
                    : @"\\?\" + extendedPath;
            }
            IntPtr handle = FindFirstFileExW(extendedPath.TrimEnd('\\') + @"\*", 1, out Win32FindData data,
                0, IntPtr.Zero, 2);
            if (handle == new IntPtr(-1))
                return new StorageDirectoryReadResult([], true);

            var entries = new List<StorageDirectoryEntry>();
            int skippedLinks = 0;
            try
            {
                do
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    string name = data.FileName;
                    if (name == "." || name == "..") continue;
                    bool directory = (data.FileAttributes & 0x10) != 0;
                    bool reparsePoint = (data.FileAttributes & 0x400) != 0;
                    // A link to a folder could loop or double count, so it is left out and counted.
                    // A file with a reparse tag (a link, a cloud placeholder) has its own size here.
                    if (reparsePoint && directory) { skippedLinks++; continue; }
                    long size = directory ? 0 : ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
                    entries.Add(new StorageDirectoryEntry(name, size, directory, reparsePoint));
                }
                while (FindNextFileW(handle, out data));
            }
            finally
            {
                FindClose(handle);
            }
            return new StorageDirectoryReadResult(entries, false, skippedLinks);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            internal uint FileAttributes;
            private System.Runtime.InteropServices.ComTypes.FILETIME _creationTime;
            private System.Runtime.InteropServices.ComTypes.FILETIME _lastAccessTime;
            private System.Runtime.InteropServices.ComTypes.FILETIME _lastWriteTime;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            private readonly uint _reserved0;
            private readonly uint _reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] private readonly string _alternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string fileName, int infoLevel,
            out Win32FindData findData, int searchOperation, IntPtr searchFilter, int additionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool FindNextFileW(IntPtr handle, out Win32FindData findData);

        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr handle);
    }
}
