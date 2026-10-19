using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KillerShell.Services
{
    /// <summary>
    /// Writes a whole file so that a failure at any point leaves the file that was there before
    /// exactly as it was. Used by the editor's Save and the elevated save helper.
    /// </summary>
    /// <remarks>
    /// A plain File.WriteAllText or FileMode.Create truncates the old file first and writes
    /// second, so a full disk, a dropped share or another program's byte-range lock in between
    /// leaves an empty or partial file where the document used to be.
    ///
    /// The normal path writes the new bytes to a temporary file beside the target, flushes them
    /// to disk and then swaps it in with File.Replace (ReplaceFile), which keeps the old file's
    /// attributes, permissions, creation time and alternate streams such as the
    /// downloaded-from-the-internet mark. ReplaceFile leaves the old file in place when it fails.
    ///
    /// Some targets must keep their file identity: a file with more than one name (hard links)
    /// would be split from its other names, and a symbolic link would be replaced by a plain
    /// file. Those, a folder that refuses new files, and a swap Windows refuses (another program
    /// has the file open without delete sharing) are written in place instead. In place, the
    /// whole range is locked and the space is reserved before any byte changes, so another
    /// program's lock or a full disk fails without touching the file; a failure during the write
    /// itself puts the original bytes back.
    /// </remarks>
    internal static class SafeFileWrite
    {
        internal static void Write(string target, byte[] data)
        {
            string path = Path.GetFullPath(target);
            if (!File.Exists(path))
            {
                WriteNew(path, data);
                return;
            }

            // Opening for writing without truncating is the access check. It changes nothing and
            // throws UnauthorizedAccessException for a read-only or protected file, which the
            // editor uses to offer its elevated retry.
            int links;
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                links = LinkCount(probe.SafeFileHandle);

            bool reparse = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            if (links > 1 || reparse)
            {
                WriteInPlace(path, data);
                return;
            }

            string temp;
            try { temp = WriteTemp(path, data); }
            catch (UnauthorizedAccessException)
            {
                // The folder refuses new files but the file itself is writable.
                WriteInPlace(path, data);
                return;
            }

            try
            {
                File.Replace(temp, path, null, ignoreMetadataErrors: false);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // ReplaceFile keeps the old file under its own name when it fails.
                TryDelete(temp);
            }
            WriteInPlace(path, data);
        }

        private static void WriteNew(string path, byte[] data)
        {
            string temp = WriteTemp(path, data);
            try { File.Move(temp, path); }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        /// <summary>Writes the complete content to a new file beside <paramref name="path"/> and
        /// flushes it to disk. Returns its path; nothing is left behind when this throws.</summary>
        private static string WriteTemp(string path, byte[] data)
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            string temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                using var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
                return temp;
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        private static void WriteInPlace(string path, byte[] data)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            long oldLength = fs.Length;
            long span = Math.Max(Math.Max(oldLength, data.Length), 1);

            // Another program's byte-range lock anywhere in the range makes this throw before
            // anything is written.
            fs.Lock(0, span);
            try
            {
                var original = new byte[oldLength];
                int read = 0;
                while (read < original.Length)
                {
                    int n = fs.Read(original, read, original.Length - read);
                    if (n == 0) break;
                    read += n;
                }

                // Growing first claims the space; a full disk fails here with the content intact.
                if (data.Length > oldLength) fs.SetLength(data.Length);

                try
                {
                    fs.Position = 0;
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                    if (data.Length < oldLength) fs.SetLength(data.Length);
                }
                catch
                {
                    try
                    {
                        fs.Position = 0;
                        fs.Write(original, 0, read);
                        fs.SetLength(read);
                        fs.Flush(true);
                    }
                    catch { /* the original error is the one to report */ }
                    throw;
                }
            }
            finally
            {
                try { fs.Unlock(0, span); } catch { }
            }
        }

        private static int LinkCount(SafeFileHandle handle) =>
            GetFileInformationByHandle(handle, out ByHandleFileInformation info) ? (int)info.NumberOfLinks : 1;

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
    }
}
