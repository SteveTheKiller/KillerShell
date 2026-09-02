using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace KillerShell.Services
{
    internal readonly struct StorageDirectoryEntry
    {
        internal string Name { get; }
        internal long Size { get; }
        internal bool IsDirectory { get; }

        internal StorageDirectoryEntry(string name, long size, bool isDirectory)
        {
            Name = name;
            Size = size;
            IsDirectory = isDirectory;
        }
    }

    internal readonly struct StorageDirectoryReadResult
    {
        internal IReadOnlyList<StorageDirectoryEntry> Entries { get; }
        internal bool Skipped { get; }

        internal StorageDirectoryReadResult(IReadOnlyList<StorageDirectoryEntry> entries, bool skipped)
        {
            Entries = entries;
            Skipped = skipped;
        }
    }

    internal static class StorageDirectoryReader
    {
        internal static StorageDirectoryReadResult Read(string path, CancellationToken cancellationToken)
        {
            IntPtr handle = FindFirstFileExW("\\\\?\\" + path + "\\*", 1, out Win32FindData data,
                0, IntPtr.Zero, 2);
            if (handle == new IntPtr(-1))
                return new StorageDirectoryReadResult(Array.Empty<StorageDirectoryEntry>(), true);

            var entries = new List<StorageDirectoryEntry>();
            try
            {
                do
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    string name = data.FileName;
                    if (name == "." || name == "..") continue;
                    bool directory = (data.FileAttributes & 0x10) != 0;
                    bool reparsePoint = (data.FileAttributes & 0x400) != 0;
                    if (reparsePoint) continue;
                    long size = directory ? 0 : ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
                    entries.Add(new StorageDirectoryEntry(name, size, directory));
                }
                while (FindNextFileW(handle, out data));
            }
            finally
            {
                FindClose(handle);
            }
            return new StorageDirectoryReadResult(entries, false);
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
            private uint _reserved0;
            private uint _reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] private string _alternateFileName;
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
