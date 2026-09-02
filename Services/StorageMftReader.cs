using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace KillerShell.Services
{
    internal sealed class StorageMftEntry
    {
        internal ulong Id { get; set; }
        internal ulong ParentId { get; set; }
        internal string Name { get; set; } = string.Empty;
        internal uint Attributes { get; set; }
        internal long Size { get; set; } = -1;
        internal bool IsDirectory => (Attributes & FileAttributeDirectory) != 0;
        internal bool IsReparsePoint => (Attributes & FileAttributeReparsePoint) != 0;

        private const uint FileAttributeDirectory = 0x10;
        private const uint FileAttributeReparsePoint = 0x400;
    }

    internal readonly struct StorageMftReadResult
    {
        internal ulong TargetId { get; }
        internal IReadOnlyDictionary<ulong, StorageMftEntry> Entries { get; }

        internal StorageMftReadResult(
            ulong targetId, IReadOnlyDictionary<ulong, StorageMftEntry> entries)
        {
            TargetId = targetId;
            Entries = entries;
        }
    }

    internal static class StorageMftReader
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x80;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint FileShareDelete = 0x4;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FsctlEnumUsnData = 0x000900B3;
        private const int ErrorHandleEof = 38;

        internal static bool CanUse(string target, bool elevated)
        {
            if (!elevated || target.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            try
            {
                string? root = Path.GetPathRoot(target);
                return !string.IsNullOrEmpty(root)
                    && root!.Length >= 2
                    && new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryRead(
            string target, CancellationToken cancellationToken, out StorageMftReadResult result)
        {
            result = default;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string drive = Path.GetPathRoot(target)![..2];
                using var volume = CreateFileW(@"\\.\" + drive, GenericRead,
                    FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (volume.IsInvalid) return false;

                ulong targetId;
                using (var targetHandle = CreateFileW(target, 0,
                    FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
                    FileFlagBackupSemantics, IntPtr.Zero))
                {
                    if (targetHandle.IsInvalid ||
                        !GetFileInformationByHandle(targetHandle, out ByHandleFileInformation targetInfo))
                        return false;
                    targetId = NormalizeFileId(((ulong)targetInfo.FileIndexHigh << 32) | targetInfo.FileIndexLow);
                }

                Dictionary<ulong, StorageMftEntry>? entries = ReadEntries(volume, cancellationToken);
                if (entries == null || !entries.ContainsKey(targetId)) return false;

                foreach (StorageMftEntry entry in entries.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entry.IsDirectory && !entry.IsReparsePoint)
                        entry.Size = GetFileSizeById(volume, entry.Id);
                }

                result = new StorageMftReadResult(targetId, entries);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        internal static Dictionary<ulong, StorageMftEntry>? ParseRecords(
            byte[] buffer, int byteCount)
        {
            if (byteCount < 8 || byteCount > buffer.Length) return null;
            var records = new Dictionary<ulong, StorageMftEntry>();
            int offset = 8;
            while (offset + 60 <= byteCount)
            {
                int length = BitConverter.ToInt32(buffer, offset);
                if (length < 60 || offset + length > byteCount) return null;
                ushort major = BitConverter.ToUInt16(buffer, offset + 4);
                if (major == 2)
                {
                    ushort nameLength = BitConverter.ToUInt16(buffer, offset + 56);
                    ushort nameOffset = BitConverter.ToUInt16(buffer, offset + 58);
                    if (nameOffset + nameLength <= length)
                    {
                        ulong id = NormalizeFileId(BitConverter.ToUInt64(buffer, offset + 8));
                        records[id] = new StorageMftEntry
                        {
                            Id = id,
                            ParentId = NormalizeFileId(BitConverter.ToUInt64(buffer, offset + 16)),
                            Attributes = BitConverter.ToUInt32(buffer, offset + 52),
                            Name = Encoding.Unicode.GetString(buffer, offset + nameOffset, nameLength),
                        };
                    }
                }
                offset += length;
            }
            return records;
        }

        private static Dictionary<ulong, StorageMftEntry>? ReadEntries(
            SafeFileHandle volume, CancellationToken cancellationToken)
        {
            const int BufferSize = 1024 * 1024;
            var output = new byte[BufferSize];
            var query = new MftEnumData
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue,
            };
            var records = new Dictionary<ulong, StorageMftEntry>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool ok = DeviceIoControl(volume, FsctlEnumUsnData, ref query, Marshal.SizeOf(query),
                    output, output.Length, out int bytes, IntPtr.Zero);
                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();
                    return error == ErrorHandleEof ? records : null;
                }
                if (bytes < 8) return null;
                query.StartFileReferenceNumber = BitConverter.ToUInt64(output, 0);
                Dictionary<ulong, StorageMftEntry>? batch =
                    ParseRecords(output, bytes);
                if (batch == null) return null;
                foreach (var pair in batch) records[pair.Key] = pair.Value;
            }
        }

        private static long GetFileSizeById(SafeFileHandle volume, ulong id)
        {
            var descriptor = new FileIdDescriptor
            {
                Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
                Type = 0,
                FileId = unchecked((long)id),
            };
            using var file = OpenFileById(volume, ref descriptor, FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, FileFlagBackupSemantics);
            return file.IsInvalid || !GetFileSizeEx(file, out long size) ? -1 : size;
        }

        private static ulong NormalizeFileId(ulong id) => id & 0x0000FFFFFFFFFFFFUL;

        [StructLayout(LayoutKind.Sequential)]
        private struct MftEnumData
        {
            internal ulong StartFileReferenceNumber;
            internal long LowUsn;
            internal long HighUsn;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdDescriptor
        {
            internal uint Size;
            internal int Type;
            internal long FileId;
            internal long ExtendedFileId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
            ref MftEnumData input, int inputSize, [Out] byte[] output, int outputSize,
            out int bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenFileById(SafeFileHandle volume,
            ref FileIdDescriptor fileId, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint flagsAndAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileSizeEx(SafeFileHandle file, out long fileSize);
    }
}
