using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace KillerShell.Services
{
    // ═══════════════════════════════════════════════════════════
    //  FILE OPERATIONS  -  copy, move, delete, rename, new folder
    // ═══════════════════════════════════════════════════════════
    // Hand-rolled rather than handed to the shell's IFileOperation, so the progress and conflict
    // UI can be KillerShell's rather than Windows'. That trade is the whole reason this file
    // exists, and it means everything the shell would have done for free is done here instead:
    // recursive enumeration, per-byte progress, conflict resolution, cancellation.
    //
    // ONE EXCEPTION: the Recycle Bin. There is no managed route to it and no documented file
    // format to write - a "deleted" file is a rename plus an entry in a per-volume index that
    // only the shell maintains. Recycling therefore calls SHFileOperation with FOF_ALLOWUNDO,
    // which is also what gives you Ctrl+Z in Explorer afterwards. Everything else is ours.
    //
    // All of this runs OFF the UI thread. Copying a folder is an unbounded amount of work and
    // the callbacks below are the only things that touch the caller, so the caller is
    // responsible for marshalling them back to the dispatcher.

    public enum FileOpKind { Copy, Move, Delete, Recycle }

    /// <summary>What to do about a target that already exists.</summary>
    public enum ConflictChoice { Replace, Skip, KeepBoth, Cancel }

    /// <summary>Both sides of a collision, so the dialog can show what is being overwritten.</summary>
    public sealed class ConflictInfo
    {
        public string SourcePath = string.Empty;
        public string TargetPath = string.Empty;
        public long   SourceSize;
        public long   TargetSize;
        public DateTime SourceModified;
        public DateTime TargetModified;
        public bool   IsDirectory;
    }

    public sealed class FileOpProgress
    {
        public string CurrentFile = string.Empty;
        public int    ItemsDone;
        public int    ItemsTotal;
        public long   BytesDone;
        public long   BytesTotal;
    }

    public sealed class FileOpResult
    {
        public int Succeeded;
        public int Skipped;
        public bool Canceled;
        public readonly List<(string Path, string Error)> Failed = new();
    }

    public static class FileOps
    {
        // Big enough that the syscall overhead disappears on a network share, small enough that
        // the progress bar still moves inside a single large file. A whole-file File.Copy would
        // be marginally faster and would freeze the bar for the length of an ISO.
        private const int CopyBufferSize = 1 << 20;   // 1 MB

        /// <summary>
        /// One unit of work: a single FILE to copy or move. Directories are not units - they are
        /// walked, created at the target, and their files become units. That is what lets the
        /// progress count mean something on a folder drop.
        /// </summary>
        private sealed class WorkItem
        {
            public string Source = string.Empty;
            public string Target = string.Empty;
            public long   Size;
        }

        // ── Planning ─────────────────────────────────────────────

        /// <summary>
        /// Flattens the sources into per-file work under <paramref name="targetDir"/>, creating
        /// no directories yet. Runs before anything is written so the total is known up front -
        /// a progress bar that discovers its own length as it goes is not a progress bar.
        /// </summary>
        private static List<WorkItem> Plan(IEnumerable<string> sources, string targetDir,
                                           List<string> dirsToCreate, CancellationToken ct)
        {
            var work = new List<WorkItem>();

            foreach (string src in sources)
            {
                ct.ThrowIfCancellationRequested();

                if (File.Exists(src))
                {
                    long size;
                    try { size = new FileInfo(src).Length; } catch { size = 0; }
                    work.Add(new WorkItem
                    {
                        Source = src,
                        Target = Path.Combine(targetDir, Path.GetFileName(src)),
                        Size   = size,
                    });
                }
                else if (Directory.Exists(src))
                {
                    string root = Path.Combine(targetDir, Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar)));
                    dirsToCreate.Add(root);
                    WalkDirectory(src, root, work, dirsToCreate, ct);
                }
                // Vanished between selection and the drop: nothing to do, and not an error worth
                // stopping a batch of fifty for.
            }

            return work;
        }

        private static void WalkDirectory(string srcDir, string dstDir, List<WorkItem> work,
                                          List<string> dirsToCreate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                foreach (var d in Directory.EnumerateDirectories(srcDir))
                {
                    string sub = Path.Combine(dstDir, Path.GetFileName(d));
                    dirsToCreate.Add(sub);
                    WalkDirectory(d, sub, work, dirsToCreate, ct);
                }

                foreach (var f in Directory.EnumerateFiles(srcDir))
                {
                    long size;
                    try { size = new FileInfo(f).Length; } catch { size = 0; }
                    work.Add(new WorkItem
                    {
                        Source = f,
                        Target = Path.Combine(dstDir, Path.GetFileName(f)),
                        Size   = size,
                    });
                }
            }
            catch (UnauthorizedAccessException) { /* walked what we could see */ }
            catch (IOException) { }
        }

        // ── Copy / move ──────────────────────────────────────────

        /// <summary>
        /// Copies or moves <paramref name="sources"/> into <paramref name="targetDir"/>.
        /// </summary>
        /// <param name="onConflict">
        /// Asked once per collision. The caller marshals to the UI thread and may return the same
        /// answer for the rest of the batch by never asking again - "apply to all" is the
        /// caller's state, not ours, because only the caller knows whether it showed a checkbox.
        /// </param>
        public static FileOpResult CopyOrMove(IEnumerable<string> sources, string targetDir, bool move,
                                              Func<ConflictInfo, ConflictChoice> onConflict,
                                              Action<FileOpProgress> onProgress,
                                              CancellationToken ct)
        {
            var result = new FileOpResult();
            var dirsToCreate = new List<string>();
            List<WorkItem> work;

            try { work = Plan(sources, targetDir, dirsToCreate, ct); }
            catch (OperationCanceledException) { result.Canceled = true; return result; }

            // A same-volume MOVE of a whole directory is a rename: one call, no walk, no byte
            // copying. Worth catching before the per-file loop, because moving a 40 GB folder
            // across the same disk should be instant rather than a progress bar.
            if (move && TryFastMoveDirectories(sources, targetDir, onConflict, result, ct))
                return result;

            foreach (string d in dirsToCreate)
            {
                try { Directory.CreateDirectory(d); }
                catch (Exception ex) { result.Failed.Add((d, ex.Message)); }
            }

            long bytesTotal = 0;
            foreach (var w in work) bytesTotal += w.Size;

            var progress = new FileOpProgress { ItemsTotal = work.Count, BytesTotal = bytesTotal };

            foreach (var item in work)
            {
                if (ct.IsCancellationRequested) { result.Canceled = true; return result; }

                string target = item.Target;

                if (File.Exists(target) || Directory.Exists(target))
                {
                    var choice = onConflict(BuildConflictInfo(item.Source, target));
                    if (choice == ConflictChoice.Cancel) { result.Canceled = true; return result; }
                    if (choice == ConflictChoice.Skip)   { result.Skipped++; progress.ItemsDone++; continue; }
                    if (choice == ConflictChoice.KeepBoth) target = UniqueName(target);
                    // Replace falls through - the copy below overwrites.
                }

                progress.CurrentFile = item.Source;
                onProgress(progress);

                try
                {
                    if (move) MoveFile(item.Source, target, ref progress, onProgress, ct);
                    else      CopyFile(item.Source, target, ref progress, onProgress, ct);
                    result.Succeeded++;
                }
                catch (OperationCanceledException) { result.Canceled = true; return result; }
                catch (Exception ex) { result.Failed.Add((item.Source, ex.Message)); }

                progress.ItemsDone++;
                onProgress(progress);
            }

            // A move leaves the source directory tree standing once its files are gone.
            if (move) RemoveEmptySourceDirs(sources);

            return result;
        }

        /// <summary>
        /// Same-volume directory moves, done as renames. Returns true only if EVERY source was
        /// handled this way - a mixed batch falls back to the general path rather than doing half
        /// the work here and half there.
        /// </summary>
        private static bool TryFastMoveDirectories(IEnumerable<string> sources, string targetDir,
                                                   Func<ConflictInfo, ConflictChoice> onConflict,
                                                   FileOpResult result, CancellationToken ct)
        {
            var list = new List<string>(sources);
            foreach (string s in list)
                if (!Directory.Exists(s) || !SameVolume(s, targetDir)) return false;

            foreach (string s in list)
            {
                if (ct.IsCancellationRequested) { result.Canceled = true; return true; }

                string target = Path.Combine(targetDir, Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar)));

                if (Directory.Exists(target) || File.Exists(target))
                {
                    var choice = onConflict(BuildConflictInfo(s, target));
                    if (choice == ConflictChoice.Cancel) { result.Canceled = true; return true; }
                    if (choice == ConflictChoice.Skip)   { result.Skipped++; continue; }
                    if (choice == ConflictChoice.KeepBoth) target = UniqueName(target);
                    // Replace on a DIRECTORY is a merge, which a rename cannot express - bail to
                    // the general path and let the per-file loop resolve it file by file.
                    else return false;
                }

                try { Directory.Move(s, target); result.Succeeded++; }
                catch (Exception ex) { result.Failed.Add((s, ex.Message)); }
            }

            return true;
        }

        private static void CopyFile(string src, string dst, ref FileOpProgress progress,
                                     Action<FileOpProgress> onProgress, CancellationToken ct)
        {
            using var input  = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBufferSize);
            using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);

            var buffer = new byte[CopyBufferSize];
            int read;
            long sinceReport = 0;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);

                progress.BytesDone += read;
                sinceReport += read;

                // Report every few MB rather than every buffer: a dispatcher hop per megabyte on
                // a fast NVMe drive is thousands of UI callbacks a second, which costs more than
                // the copy does.
                if (sinceReport >= 4 * CopyBufferSize) { sinceReport = 0; onProgress(progress); }
            }

            // Timestamps should survive a copy - it is the same file as far as anyone reading it
            // is concerned. Failure here is not worth failing the copy over.
            try { File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src)); } catch { }
        }

        private static void MoveFile(string src, string dst, ref FileOpProgress progress,
                                     Action<FileOpProgress> onProgress, CancellationToken ct)
        {
            // Same volume: a rename, instantaneous whatever the size. Different volume: there is
            // no such thing as a cross-volume move, so it is a copy followed by a delete, and the
            // delete only happens once the copy has actually landed.
            if (SameVolume(src, dst))
            {
                if (File.Exists(dst)) File.Delete(dst);   // Replace was already agreed above
                File.Move(src, dst);
                progress.BytesDone += SafeSize(src);
                return;
            }

            CopyFile(src, dst, ref progress, onProgress, ct);
            try { File.Delete(src); }
            catch { /* copy landed; a locked source is not worth undoing it for */ }
        }

        private static void RemoveEmptySourceDirs(IEnumerable<string> sources)
        {
            foreach (string s in sources)
            {
                if (!Directory.Exists(s)) continue;
                try
                {
                    // Only if genuinely empty. A directory still holding a file that failed to
                    // move must survive, or the failure becomes data loss.
                    if (Directory.GetFileSystemEntries(s).Length == 0) Directory.Delete(s);
                    else PruneEmpty(s);
                }
                catch { }
            }
        }

        private static void PruneEmpty(string dir)
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir)) PruneEmpty(d);
                if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
            }
            catch { }
        }

        // ── Delete ───────────────────────────────────────────────

        /// <summary>Permanent delete. Ours, recursive, cancelable.</summary>
        public static FileOpResult Delete(IEnumerable<string> paths, Action<FileOpProgress> onProgress,
                                          CancellationToken ct)
        {
            var list = new List<string>(paths);
            var result = new FileOpResult();
            var progress = new FileOpProgress { ItemsTotal = list.Count };

            foreach (string p in list)
            {
                if (ct.IsCancellationRequested) { result.Canceled = true; return result; }

                progress.CurrentFile = p;
                onProgress(progress);

                try
                {
                    if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                    else if (File.Exists(p)) File.Delete(p);
                    result.Succeeded++;
                }
                catch (Exception ex) { result.Failed.Add((p, ex.Message)); }

                progress.ItemsDone++;
                onProgress(progress);
            }

            return result;
        }

        /// <summary>
        /// Recycle Bin. THE one shell call in this file - see the header. FOF_ALLOWUNDO is what
        /// makes it a recycle rather than a delete, and it is also what puts the operation on
        /// Explorer's undo stack, so Ctrl+Z there brings the files back.
        /// </summary>
        public static FileOpResult Recycle(IEnumerable<string> paths)
        {
            var list = new List<string>(paths);
            var result = new FileOpResult();
            if (list.Count == 0) return result;

            // Double-null terminated, and the whole block terminated again - the API reads until
            // it sees an empty string, so a single terminator would run off the end.
            string joined = string.Join("\0", list) + "\0\0";

            var op = new SHFILEOPSTRUCT
            {
                wFunc  = FO_DELETE,
                pFrom  = joined,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
            };

            int rc = SHFileOperation(ref op);

            if (rc != 0 || op.fAnyOperationsAborted)
            {
                result.Canceled = op.fAnyOperationsAborted;
                if (rc != 0) result.Failed.Add((list[0], "SHFileOperation returned " + rc));
            }
            else result.Succeeded = list.Count;

            return result;
        }

        // ── Rename / new folder ──────────────────────────────────

        /// <summary>Renames in place. Returns null on success, or the error to show.</summary>
        public static string? Rename(string path, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return "empty name";
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "invalid characters";

            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            string target = Path.Combine(dir, newName);

            if (string.Equals(target, path, StringComparison.Ordinal)) return null;   // genuinely unchanged

            // A case-only rename ("readme" -> "README") is a real rename and must skip the
            // exists check: on a case-insensitive volume that check would find the very file
            // being renamed and refuse. Ordinal above already let the true no-op out.
            bool caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) return "already exists";

            try
            {
                if (Directory.Exists(path)) Directory.Move(path, target);
                else File.Move(path, target);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// Creates a new folder under <paramref name="parent"/>, numbering it the way Explorer
        /// does when the plain name is taken. Returns the created path, or null on failure.
        /// </summary>
        public static string? NewFolder(string parent, string baseName)
        {
            string path = Path.Combine(parent, baseName);
            int n = 2;
            while (Directory.Exists(path) || File.Exists(path))
                path = Path.Combine(parent, baseName + " (" + n++ + ")");

            try { Directory.CreateDirectory(path); return path; }
            catch { return null; }
        }

        // ── Helpers ──────────────────────────────────────────────

        /// <summary>"report.txt" -> "report (2).txt", skipping any number already taken.</summary>
        private static string UniqueName(string path)
        {
            string dir  = Path.GetDirectoryName(path) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext  = Path.GetExtension(path);

            int n = 2;
            string candidate;
            do { candidate = Path.Combine(dir, stem + " (" + n++ + ")" + ext); }
            while (File.Exists(candidate) || Directory.Exists(candidate));

            return candidate;
        }

        private static ConflictInfo BuildConflictInfo(string src, string dst)
        {
            var info = new ConflictInfo { SourcePath = src, TargetPath = dst };
            try
            {
                if (Directory.Exists(dst))
                {
                    info.IsDirectory     = true;
                    info.TargetModified  = Directory.GetLastWriteTime(dst);
                }
                else
                {
                    var f = new FileInfo(dst);
                    info.TargetSize     = f.Length;
                    info.TargetModified = f.LastWriteTime;
                }

                if (Directory.Exists(src)) info.SourceModified = Directory.GetLastWriteTime(src);
                else
                {
                    var f = new FileInfo(src);
                    info.SourceSize     = f.Length;
                    info.SourceModified = f.LastWriteTime;
                }
            }
            catch { /* a partial card beats no card */ }
            return info;
        }

        private static long SafeSize(string p)
        {
            try { return new FileInfo(p).Length; } catch { return 0; }
        }

        /// <summary>
        /// Same volume, by root path. Deliberately not a mount-point-aware test: getting that
        /// right needs GetVolumePathName, and being wrong here only costs a copy-then-delete
        /// where a rename would have done - slower, never incorrect.
        /// </summary>
        private static bool SameVolume(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)),
                                     Path.GetPathRoot(Path.GetFullPath(b)),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ── SHFileOperation (Recycle Bin only) ───────────────────

        private const uint FO_DELETE            = 0x0003;
        private const ushort FOF_ALLOWUNDO      = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_WANTNUKEWARNING= 0x4000;   // still warn if it CANNOT be recycled

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint   wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
    }
}
