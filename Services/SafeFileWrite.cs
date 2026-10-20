using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KillerShell.Services
{
    /// <summary>
    /// Writes a whole file, restoring the original after a failed write or retaining an emergency
    /// copy when restoration also fails. Used by the editor's Save and the elevated save helper.
    /// </summary>
    /// <remarks>
    /// A plain File.WriteAllText or FileMode.Create truncates the old file first and writes
    /// second, so a full disk, a dropped share or another program's byte-range lock in between
    /// leaves an empty or partial file where the document used to be.
    ///
    /// The normal path writes the new bytes to a temporary file beside the target, flushes them
    /// to disk and then swaps it in with File.Replace (ReplaceFile), which keeps the old file's
    /// attributes, permissions, creation time and alternate streams such as the
    /// downloaded-from-the-internet mark. The swap names a backup, so a failed swap always keeps
    /// the old file (see Write).
    ///
    /// Some targets must keep their file identity: a file with more than one name (hard links)
    /// would be split from its other names, and a symbolic link would be replaced by a plain
    /// file. Those, a folder that refuses new files, a disk without room for a second full copy,
    /// and a swap Windows refuses (another program has the file open without delete sharing) are
    /// written in place instead. In place, the whole range is locked and the space is reserved
    /// before any byte changes, so another program's lock or a full disk fails without touching
    /// the file; a failure during the write itself puts the original bytes back. If restoration
    /// also fails, an emergency copy is attempted in the user's temp folder and its result reported.
    /// </remarks>
    internal static class SafeFileWrite
    {
        internal static void Write(string target, byte[] data)
            => Write(target, data, OpenInPlace);

        internal static void Write(string target, byte[] data, Func<string, FileStream> openInPlace)
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
                WriteInPlace(path, data, openInPlace);
                return;
            }

            string temp;
            try { temp = WriteTemp(path, data); }
            catch (UnauthorizedAccessException)
            {
                // The folder refuses new files but the file itself is writable.
                WriteInPlace(path, data, openInPlace);
                return;
            }
            catch (IOException ex) when (IsOutOfSpace(ex))
            {
                // Not enough room for a second full copy. Writing in place only needs the space
                // the file grows by, which it reserves before changing anything, so a save that
                // fits by reusing the old file's space still succeeds.
                WriteInPlace(path, data, openInPlace);
                return;
            }

            // Always swapped with a backup name. ReplaceFile documents that without one, failing
            // to rename the new file into place (ERROR_UNABLE_TO_MOVE_REPLACEMENT) leaves the old
            // file already gone; with one, every failure keeps the old file under its own name or
            // the backup name. Nothing that may be the only copy is deleted until the target is
            // back in place.
            string backup = SiblingName(path, ".bak");
            try
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: false);
                TryDelete(backup);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (!File.Exists(path))
                {
                    if (TryMove(backup, path)) { /* the old file is back; write in place below */ }
                    else if (TryMove(temp, path)) { TryDelete(backup); return; }   // the new file is in place
                    else throw;   // both copies stay beside the target for the user to recover
                }
                TryDelete(temp);
            }
            WriteInPlace(path, data, openInPlace);
        }

        private static string SiblingName(string path, string extension) =>
            Path.Combine(Path.GetDirectoryName(path) ?? ".",
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);

        // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL and ERROR_DISK_QUOTA_EXCEEDED.
        private static bool IsOutOfSpace(IOException ex) =>
            (ex.HResult & 0xFFFF) is 0x27 or 0x70 or 0x50F;

        private static bool TryMove(string from, string to)
        {
            try
            {
                if (!File.Exists(from)) return false;
                File.Move(from, to);
                return true;
            }
            catch { return false; }
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
            string temp = SiblingName(path, ".tmp");
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

        private static FileStream OpenInPlace(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);

        private static void WriteInPlace(string path, byte[] data, Func<string, FileStream> openInPlace)
        {
            var fs = openInPlace(path);
            try { WriteInPlaceCore(path, data, fs); }
            catch
            {
                // A failed buffered write can also make Dispose throw. Keep the recovery result.
                try { fs.Dispose(); } catch { }
                throw;
            }
            fs.Dispose();
        }

        private static void WriteInPlaceCore(string path, byte[] data, FileStream fs)
        {
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
                    if (n == 0) throw new EndOfStreamException("Could not read the complete original before saving.");
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
                catch (Exception writeError)
                {
                    try
                    {
                        fs.Position = 0;
                        fs.Write(original, 0, read);
                        fs.SetLength(read);
                        fs.Flush(true);
                    }
                    catch (Exception restoreError)
                    {
                        throw PreserveOriginal(path, original, writeError, restoreError);
                    }
                    throw;
                }
            }
            finally
            {
                try { fs.Unlock(0, span); } catch { }
            }
        }

        private static IOException PreserveOriginal(string path, byte[] original, Exception writeError, Exception restoreError)
        {
            string? recovery = null;
            try
            {
                recovery = Path.Combine(Path.GetTempPath(), "KillerShell-recovery-" + Guid.NewGuid().ToString("N") + ".original");
                using (var copy = new FileStream(recovery, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    copy.Write(original, 0, original.Length);
                    copy.Flush(true);
                }
            }
            catch (Exception recoveryError)
            {
                return new IOException("Save failed for '" + path + "': " + writeError.Message
                    + " Restoration failed: " + restoreError.Message + " Emergency recovery failed: " + recoveryError.Message
                    + (recovery == null ? "" : " Attempted recovery path (not a complete backup): " + recovery),
                    new AggregateException(writeError, restoreError, recoveryError));
            }
            var error = new IOException("Save failed for '" + path + "': " + writeError.Message
                + " Restoration failed: " + restoreError.Message + " Complete original saved to: " + recovery,
                new AggregateException(writeError, restoreError));
            error.Data["RecoveryPath"] = recovery;
            return error;
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
