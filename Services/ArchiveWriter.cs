using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

// Writing INSIDE an archive: add, delete and rename entries. Pure, the same way
// ArchiveProvider is pure - no WPF, no window, no UI state - so this half can be
// transliterated and tested against real archives the way the read half was.
//
// ── WHAT IS WRITABLE, AND WHY IT IS ONLY ZIP ─────────────────────────────
//   .zip           WRITABLE. ZipArchive is part of .NET Framework itself, an entry list
//                  round-trips through it without anything being invented, and the format
//                  stores nothing this code cannot reproduce.
//   .tar .tar.gz   READ-ONLY, deliberately, not for lack of time. The tar reader in
//   .tgz           ArchiveProvider lists regular files and directories and SKIPS links,
//                  devices, FIFOs, sparse members and pax records - correct for a listing and
//                  fatal for a rewrite, because an archive rebuilt from what the reader can
//                  see would silently DROP every member it does not model. Doing it properly
//                  means a second hand-rolled format implementation whose failure mode is a
//                  quietly damaged archive, and the pax bug already found in the reader is the
//                  measure of how easy that is to get wrong.
//   .gz            READ-ONLY. A lone gzip stream holds exactly one member and records no name
//                  for it, so "add a second file" has no representation in the format at all.
// rar and 7z never reach here: ArchiveProvider classifies them Unsupported, so they cannot be
// browsed and there is nothing to write into.
// An attempted write to any of these refuses with a status line. It never no-ops, and it never
// half-writes.
//
// ── HOW THE ORIGINAL SURVIVES A FAILURE ──────────────────────────────────
// Nothing is written into the archive in place, ever. Every operation builds a COMPLETE new
// archive beside the original under a .tmp name, proves that file opens and holds the expected
// number of entries, and only then swaps it in. Until the swap the original has not been
// touched by a single byte, so a crash, a power cut, a full disk or a cancel leaves it exactly
// as it was and costs nothing but the temp file. The swap itself is File.Replace with a backup,
// retried, and a two-step move as the fallback - at every instant of which one complete file
// exists under one of the two names.
//
// ZipArchiveMode.Update is deliberately NOT used, though it is the obvious route and the one
// the backlog note assumed. Two reasons, either one sufficient: it holds the ENTIRE archive in
// memory for as long as it is open, which is exactly the multi-GB case that has to keep
// working, and it rewrites the file IN PLACE on save, which is precisely the unrecoverable
// middle state the temp-and-swap exists to remove. Streaming entry by entry into a new file
// costs one recompression pass and keeps memory flat whatever the size.
namespace KillerShell.Services
{
    /// <summary>What this build can do to a given archive, beyond reading it.</summary>
    internal enum ArchiveWriteSupport
    {
        /// <summary>Not an archive, or one this build cannot open at all.</summary>
        None,
        /// <summary>Readable, but this build will not write it. See the header.</summary>
        ReadOnly,
        /// <summary>Entries can be added, deleted and renamed.</summary>
        Writable,
    }

    /// <summary>What to do when an incoming name is already in the archive.</summary>
    internal enum ArchiveCollision
    {
        /// <summary>Write nothing, and hand the colliding names back so the caller can ask.</summary>
        Report,
        /// <summary>Drop the existing entry and keep the incoming one.</summary>
        Replace,
        /// <summary>Add the incoming one under a numbered name, keeping both.</summary>
        KeepBoth,
    }

    internal sealed class ArchiveWriteResult
    {
        internal bool Ok;

        /// <summary>Str_ key describing the refusal or failure, or null when it worked.</summary>
        internal string? ErrorKey;

        /// <summary>Detail for the key's {0} slot - an exception message, a name. Not localized.</summary>
        internal string? ErrorDetail;

        /// <summary>Entries added, removed or renamed.</summary>
        internal int Changed;

        /// <summary>Entry names already present, when the collision policy was Report.</summary>
        internal readonly List<string> Collisions = [];

        /// <summary>Sources that could not be given a legal entry name, so were not added.</summary>
        internal readonly List<string> Rejected = [];
    }

    /// <summary>One file (or one empty folder, when the name ends in '/') queued for adding.</summary>
    internal sealed class ArchiveAddItem
    {
        internal string SourcePath = "";
        internal string EntryName  = "";
    }

    internal static class ArchiveWriter
    {
        private const int CopyBuffer = 81920;

        /// <summary>Temp and backup files are stamped so a leftover from a crashed run is
        /// identifiable, and so two windows writing two archives in one folder cannot collide.</summary>
        private const string TempMark = ".kswrite-";

        // ── What can be written ──────────────────────────────────
        internal static ArchiveWriteSupport WriteSupport(string? archivePath)
        {
            if (ArchiveProvider.Classify(archivePath) != ArchiveSupport.Read) return ArchiveWriteSupport.None;
            return IsZip(archivePath!) ? ArchiveWriteSupport.Writable : ArchiveWriteSupport.ReadOnly;
        }

        internal static bool CanWrite(string? archivePath)
            => WriteSupport(archivePath) == ArchiveWriteSupport.Writable;

        private static bool IsZip(string path)
            => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

        // ── Entry name hygiene (the write side of Zip Slip) ──────
        /// <summary>
        /// A name that is safe to STORE in an archive: forward slashes, relative, no drive, no
        /// "." or ".." segment, and every segment a legal file name. Null when nothing legal is
        /// left of it.
        /// </summary>
        /// <remarks>
        /// ArchiveProvider.Normalize does the equivalent on the way OUT, so a crafted archive
        /// cannot hand a traversing path to a caller. This is the same guarantee on the way IN:
        /// an entry named "../../windows/system32/x.dll" written into a user's zip is a Zip Slip
        /// payload aimed at whoever extracts it next, and refusing to create one costs nothing.
        /// Rejecting a segment carrying an invalid file-name character also keeps the virtual
        /// path separator out: '?' is in Path.GetInvalidFileNameChars(), so an entry whose name
        /// could split a virtual path in the wrong place can never be created here.
        /// </remarks>
        internal static string? SafeEntryName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            string p = name!.Replace('\\', '/');

            // A drive spec has to go before the split, or "C:/x" keeps a "C:" segment. Anything
            // before a colon is dropped rather than escaped: no legal Windows file name has one.
            int colon = p.IndexOf(':');
            if (colon >= 0) p = p[(colon + 1)..];

            var parts = new List<string>();
            foreach (string seg in p.Split('/'))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") continue;
                if (seg.IndexOfAny(BadNameChars) >= 0) return null;
                // Windows silently strips a trailing dot or space from a file name, so a segment
                // made only of them would extract to nothing at all.
                if (seg.TrimEnd(' ', '.').Length == 0) return null;
                parts.Add(seg);
            }

            return parts.Count == 0 ? null : string.Join("/", parts);
        }

        private static readonly char[] BadNameChars = Path.GetInvalidFileNameChars();

        /// <summary>True when a single name typed into the rename box can be stored as-is. A
        /// rename must not also MOVE the entry, so a separator is rejected rather than honored -
        /// and GetInvalidFileNameChars covers both separators, the colon and the '?' the virtual
        /// path is split on.</summary>
        internal static bool IsLegalLeafName(string? name)
            => !string.IsNullOrWhiteSpace(name)
               && name!.IndexOfAny(BadNameChars) < 0
               && name.TrimEnd(' ', '.').Length > 0
               && name != "." && name != "..";

        // ── Add ──────────────────────────────────────────────────
        /// <summary>
        /// Copies <paramref name="sources"/> (files and folders, from disk) into the archive
        /// under <paramref name="entryFolder"/>.
        /// </summary>
        /// <param name="collision">
        /// Report writes NOTHING and returns the clashing names, so the caller can ask before
        /// anything is destroyed. That is the default the UI uses: an existing entry is never
        /// silently overwritten.
        /// </param>
        internal static ArchiveWriteResult Add(string archivePath, string entryFolder,
                                               IEnumerable<string> sources,
                                               ArchiveCollision collision,
                                               Action<int, int, string>? progress,
                                               CancellationToken ct)
        {
            var result = new ArchiveWriteResult();
            if (!Precheck(archivePath, result)) return result;

            // The folder being browsed goes through the same hygiene as the names themselves, so
            // a virtual path that somehow carried "../" cannot aim the add outside the archive.
            string? folder = SafeEntryName(entryFolder);
            string prefix = string.IsNullOrEmpty(folder) ? "" : folder + "/";

            var adds = new List<ArchiveAddItem>();
            foreach (string src in sources)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(src)) AddFile(src, prefix + Path.GetFileName(src), adds, result);
                    else if (Directory.Exists(src)) AddFolder(src, prefix, adds, result, ct);
                    // Vanished between the drag and the drop: nothing to add, and not worth
                    // failing the other nineteen files for.
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result.Rejected.Add(src); result.ErrorDetail ??= ex.Message; }
            }

            if (adds.Count == 0)
            {
                result.ErrorKey = "Str_Status_ArchiveNothingAdded";
                return result;
            }

            // Existing names, and the incoming ones as they are decided, share one set: two
            // dropped files that would land on the same name have to collide with each other as
            // well as with the archive, or KeepBoth would hand out the same name twice.
            HashSet<string> taken;
            try { taken = ExistingNames(archivePath); }
            catch (Exception ex)
            {
                result.ErrorKey = "Str_Status_ArchiveWriteFailed";
                result.ErrorDetail = ex.Message;
                return result;
            }

            var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in adds)
            {
                if (!taken.Contains(a.EntryName)) { taken.Add(a.EntryName); continue; }

                switch (collision)
                {
                    case ArchiveCollision.Report:
                        result.Collisions.Add(a.EntryName);
                        break;
                    case ArchiveCollision.Replace:
                        replaced.Add(a.EntryName);
                        break;
                    default:
                        a.EntryName = UniqueEntryName(a.EntryName, taken);
                        taken.Add(a.EntryName);
                        break;
                }
            }

            if (result.Collisions.Count > 0)
            {
                // Nothing has been written. The caller asks, then calls again with Replace or
                // KeepBoth - which is the whole point of a policy rather than a prompt in here.
                result.ErrorKey = "Str_Status_ArchiveCollide";
                return result;
            }

            int changed = adds.Count;
            var ok = Rebuild(archivePath, raw =>
            {
                string norm = NormalizeExisting(raw);
                // Only a REPLACE drops anything that is already there, and only the exact name.
                return replaced.Contains(norm) ? null : raw;
            }, adds, progress, ct, result);

            if (ok) { result.Ok = true; result.Changed = changed; }
            return result;
        }

        private static void AddFile(string src, string? entryName, List<ArchiveAddItem> adds,
                                    ArchiveWriteResult result)
        {
            string? safe = SafeEntryName(entryName);
            if (safe == null) { result.Rejected.Add(src); return; }
            adds.Add(new ArchiveAddItem { SourcePath = src, EntryName = safe });
        }

        private static void AddFolder(string srcDir, string prefix, List<ArchiveAddItem> adds,
                                      ArchiveWriteResult result, CancellationToken ct)
        {
            string root = Path.GetFileName(srcDir.TrimEnd(Path.DirectorySeparatorChar,
                                                          Path.AltDirectorySeparatorChar));
            if (root.Length == 0) return;
            WalkFolder(srcDir, prefix + root, adds, result, ct);
        }

        private static void WalkFolder(string dir, string entryDir, List<ArchiveAddItem> adds,
                                       ArchiveWriteResult result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string[] files, subs;
            try
            {
                files = Directory.GetFiles(dir);
                subs  = Directory.GetDirectories(dir);
            }
            catch (Exception) { result.Rejected.Add(dir); return; }

            // An EMPTY folder still has to travel, or dragging one in silently adds nothing. A
            // zip stores that as a name ending in '/' with no content.
            if (files.Length == 0 && subs.Length == 0)
            {
                string? safeDir = SafeEntryName(entryDir);
                if (safeDir != null) adds.Add(new ArchiveAddItem { SourcePath = dir, EntryName = safeDir + "/" });
                return;
            }

            foreach (string f in files) AddFile(f, entryDir + "/" + Path.GetFileName(f), adds, result);
            foreach (string d in subs)
                WalkFolder(d, entryDir + "/" + Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar)),
                           adds, result, ct);
        }

        // ── New folder ──────────────────────────────────────────
        /// <summary>Creates an explicit empty-directory entry in a ZIP. A stored entry is
        /// required even when neighboring folders are merely implied by their child paths.</summary>
        internal static ArchiveWriteResult CreateFolder(string archivePath, string entryFolder,
                                                        string newName,
                                                        Action<int, int, string>? progress,
                                                        CancellationToken ct)
        {
            var result = new ArchiveWriteResult();
            if (!Precheck(archivePath, result)) return result;
            if (!IsLegalLeafName(newName))
            {
                result.ErrorKey = "Str_Status_ArchiveBadName";
                return result;
            }

            string? parent = SafeEntryName(entryFolder);
            string? full = SafeEntryName((string.IsNullOrEmpty(parent) ? "" : parent + "/")
                                         + newName.Trim());
            if (full == null)
            {
                result.ErrorKey = "Str_Status_ArchiveBadName";
                return result;
            }

            try
            {
                var existing = ExistingNames(archivePath);
                if (existing.Contains(full)
                    || existing.Any(n => n.StartsWith(full + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    result.ErrorKey = "Str_Status_ArchiveNameTaken";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.ErrorKey = "Str_Status_ArchiveWriteFailed";
                result.ErrorDetail = ex.Message;
                return result;
            }

            var adds = new List<ArchiveAddItem>
            {
                new ArchiveAddItem { EntryName = full + "/" },
            };
            if (Rebuild(archivePath, raw => raw, adds, progress, ct, result))
            {
                result.Ok = true;
                result.Changed = 1;
            }
            return result;
        }

        // ── Delete ───────────────────────────────────────────────
        /// <summary>
        /// Removes entries. A folder path removes everything beneath it, because a folder inside
        /// an archive is usually implied by the paths under it rather than stored in its own
        /// right - deleting only a stored folder entry would leave its contents behind and still
        /// listed.
        /// </summary>
        internal static ArchiveWriteResult Delete(string archivePath, IEnumerable<string> entryPaths,
                                                  Action<int, int, string>? progress,
                                                  CancellationToken ct)
        {
            var result = new ArchiveWriteResult();
            if (!Precheck(archivePath, result)) return result;

            var targets = new List<string>();
            foreach (string e in entryPaths)
            {
                string n = NormalizeExisting(e);
                if (n.Length > 0) targets.Add(n);
            }
            if (targets.Count == 0) { result.ErrorKey = "Str_Status_ArchiveEntryGone"; return result; }

            int dropped = 0;
            var ok = Rebuild(archivePath, raw =>
            {
                string norm = NormalizeExisting(raw);
                foreach (string t in targets)
                    if (norm.Equals(t, StringComparison.OrdinalIgnoreCase)
                        || norm.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase))
                    { dropped++; return null; }
                return raw;
            }, null, progress, ct, result);

            if (!ok) return result;

            if (dropped == 0)
            {
                // The rebuild produced an identical archive and has already been swapped in, so
                // nothing is damaged - but the entry the user picked was not in there, and
                // saying "deleted 0" would read as success.
                result.ErrorKey = "Str_Status_ArchiveEntryGone";
                return result;
            }

            result.Ok = true;
            result.Changed = dropped;
            return result;
        }

        // ── Rename ───────────────────────────────────────────────
        /// <summary>
        /// Renames one entry in place. A folder takes everything beneath it with it, for the same
        /// reason Delete does.
        /// </summary>
        internal static ArchiveWriteResult Rename(string archivePath, string entryPath, string newName,
                                                  Action<int, int, string>? progress,
                                                  CancellationToken ct)
        {
            var result = new ArchiveWriteResult();
            if (!Precheck(archivePath, result)) return result;

            if (!IsLegalLeafName(newName)) { result.ErrorKey = "Str_Status_ArchiveBadName"; return result; }

            string oldPath = NormalizeExisting(entryPath);
            if (oldPath.Length == 0) { result.ErrorKey = "Str_Status_ArchiveEntryGone"; return result; }

            int slash = oldPath.LastIndexOf('/');
            string parent  = slash < 0 ? "" : oldPath[..(slash + 1)];
            string newPath = parent + newName.Trim();

            // A genuine no-op, not a case-only rename: those are real and have to go through.
            if (string.Equals(newPath, oldPath, StringComparison.Ordinal))
            {
                result.Ok = true;
                return result;
            }

            HashSet<string> existing;
            try { existing = ExistingNames(archivePath); }
            catch (Exception ex)
            {
                result.ErrorKey = "Str_Status_ArchiveWriteFailed";
                result.ErrorDetail = ex.Message;
                return result;
            }

            bool caseOnly = string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly)
                foreach (string n in existing)
                    if (n.Equals(newPath, StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith(newPath + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        result.ErrorKey = "Str_Status_ArchiveNameTaken";
                        return result;
                    }

            int moved = 0;
            var ok = Rebuild(archivePath, raw =>
            {
                string norm = NormalizeExisting(raw);
                bool trailing = raw.EndsWith("/", StringComparison.Ordinal);

                if (norm.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    moved++;
                    return trailing ? newPath + "/" : newPath;
                }
                if (norm.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    moved++;
                    string tail = norm[oldPath.Length..];   // keeps its leading '/'
                    return trailing ? newPath + tail + "/" : newPath + tail;
                }
                return raw;
            }, null, progress, ct, result);

            if (!ok) return result;

            if (moved == 0) { result.ErrorKey = "Str_Status_ArchiveEntryGone"; return result; }

            result.Ok = true;
            result.Changed = moved;
            return result;
        }

        // ── The rebuild, and the swap that makes it safe ─────────
        /// <summary>
        /// Builds a complete new archive from the old one beside it, verifies it, and swaps it in.
        /// </summary>
        /// <param name="mapName">
        /// Given an existing entry's RAW name, the name it keeps (usually itself) or null to drop
        /// it. Raw rather than normalized on purpose: an entry this operation is not touching is
        /// copied through under exactly the name it had, so a write never quietly rewrites names
        /// nobody asked about.
        /// </param>
        private static bool Rebuild(string archivePath, Func<string, string?> mapName,
                                    List<ArchiveAddItem>? adds,
                                    Action<int, int, string>? progress,
                                    CancellationToken ct, ArchiveWriteResult result)
        {
            string dir  = Path.GetDirectoryName(archivePath) ?? ".";
            string leaf = Path.GetFileName(archivePath);
            string stamp = Guid.NewGuid().ToString("N")[..8];
            string temp   = Path.Combine(dir, leaf + TempMark + stamp + ".tmp");
            string backup = Path.Combine(dir, leaf + TempMark + stamp + ".bak");

            int written = 0;

            try
            {
                using (var src = ZipFile.OpenRead(archivePath))
                {
                    int total = src.Entries.Count + (adds?.Count ?? 0);
                    int done  = 0;

                    // FileMode.CreateNew, not Create: the stamped name should be unique, and if
                    // it somehow is not then something else owns that file and this must not
                    // flatten it.
                    using var outFs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                                                     FileShare.None, CopyBuffer);
                    using var dst = new ZipArchive(outFs, ZipArchiveMode.Create);

                    foreach (var e in src.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        done++;

                        string? name = mapName(e.FullName);
                        if (name == null) continue;                 // dropped

                        progress?.Invoke(done, total, name);

                        if (name.EndsWith("/", StringComparison.Ordinal))
                        {
                            // A stored folder: a name and a timestamp, no content.
                            CopyTime(dst.CreateEntry(name), e);
                            written++;
                            continue;
                        }

                        var ne = dst.CreateEntry(name, CompressionLevel.Optimal);

                        // BEFORE the stream is opened, not after. In Create mode ZipArchive
                        // freezes an entry's header the instant it is first opened for writing
                        // and throws IOException on any later change to it, so a copy that set
                        // the timestamp afterwards would lose the modified date of every entry
                        // in the archive - swallowed by CopyTime's catch, so silently.
                        CopyTime(ne, e);

                        using (var input  = e.Open())
                        using (var output = ne.Open())
                            CopyStream(input, output, ct);
                        written++;
                    }

                    foreach (var a in adds ?? [])
                    {
                        ct.ThrowIfCancellationRequested();
                        done++;
                        progress?.Invoke(done, total, a.EntryName);

                        if (a.EntryName.EndsWith("/", StringComparison.Ordinal))
                        {
                            dst.CreateEntry(a.EntryName);
                            written++;
                            continue;
                        }

                        var ne = dst.CreateEntry(a.EntryName, CompressionLevel.Optimal);

                        // Same ordering rule as the copy loop above: set it before the stream
                        // is opened or Create mode refuses it.
                        try { ne.LastWriteTime = File.GetLastWriteTime(a.SourcePath); } catch { }

                        using (var input = new FileStream(a.SourcePath, FileMode.Open, FileAccess.Read,
                                                          FileShare.ReadWrite, CopyBuffer))
                        using (var output = ne.Open())
                            CopyStream(input, output, ct);
                        written++;
                    }
                }

                // Prove it before anything replaces the original. An archive that cannot be
                // reopened, or that lost entries on the way through, must never reach the user's
                // file - and at this point nothing has: deleting the temp undoes the whole
                // operation.
                using (var check = ZipFile.OpenRead(temp))
                    if (check.Entries.Count != written)
                        throw new IOException("rebuilt archive holds " + check.Entries.Count
                                              + " entries, expected " + written);

                SwapIn(temp, archivePath, backup);
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                result.ErrorKey = "Str_Status_ArchiveCanceled";
                return false;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                result.ErrorKey = "Str_Status_ArchiveWriteFailed";
                result.ErrorDetail = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Puts the rebuilt file in the original's place without ever leaving the original
        /// missing.
        /// </summary>
        /// <remarks>
        /// File.Replace needs simultaneous exclusive access to all three paths, and an indexer or
        /// an antivirus scanner holding the archive open for a moment is enough to refuse it - so
        /// it is retried before the fallback rather than treated as fatal. The fallback is a
        /// two-step move, and its ordering is the point: the original is moved ASIDE first, so at
        /// every instant a complete file exists under one of the two names, and a failure to move
        /// the new one in puts the original straight back.
        /// </remarks>
        private static void SwapIn(string temp, string target, string backup)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    File.Replace(temp, target, backup, ignoreMetadataErrors: true);
                    TryDelete(backup);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (PlatformNotSupportedException) { break; }

                Thread.Sleep(40 * (attempt + 1));
            }

            File.Move(target, backup);
            try { File.Move(temp, target); }
            catch { File.Move(backup, target); throw; }
            TryDelete(backup);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>CopyTo cannot observe cancellation while one multi-gigabyte entry is being
        /// recompressed. Check between every buffer so Cancel has bounded response time even
        /// when an archive contains only one enormous file.</summary>
        private static void CopyStream(Stream input, Stream output, CancellationToken ct)
        {
            var buffer = new byte[CopyBuffer];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) return;
                output.Write(buffer, 0, read);
            }
        }

        private static void CopyTime(ZipArchiveEntry dst, ZipArchiveEntry src)
        {
            // Both halves throw on their own: reading a zeroed DOS timestamp raises, and setting
            // one before 1980 raises too. A timestamp is not worth failing a rewrite over.
            try { dst.LastWriteTime = src.LastWriteTime; } catch { }
        }

        // ── Shared helpers ───────────────────────────────────────
        /// <summary>Every entry name already in the archive, normalized and case-insensitive, so
        /// a collision test matches the way the listing and Windows both do.</summary>
        private static HashSet<string> ExistingNames(string archivePath)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var e in zip.Entries)
            {
                string n = NormalizeExisting(e.FullName);
                if (n.Length > 0) set.Add(n);
            }
            return set;
        }

        /// <summary>
        /// An EXISTING entry's name, reduced to the form the listing shows so comparisons line
        /// up. Deliberately not SafeEntryName: this must never reject, because a name that is
        /// already in the archive still has to be matchable and still has to be copied through.
        /// </summary>
        private static string NormalizeExisting(string raw)
            => raw.Replace('\\', '/').Trim('/');

        /// <summary>"docs/report.txt" -> "docs/report (2).txt", skipping any number taken.</summary>
        private static string UniqueEntryName(string name, HashSet<string> taken)
        {
            int slash = name.LastIndexOf('/');
            string dir  = slash < 0 ? "" : name[..(slash + 1)];
            string leaf = slash < 0 ? name : name[(slash + 1)..];

            int dot = leaf.LastIndexOf('.');
            string stem = dot <= 0 ? leaf : leaf[..dot];
            string ext  = dot <= 0 ? ""   : leaf[dot..];

            int n = 2;
            string candidate;
            do { candidate = dir + stem + " (" + n++ + ")" + ext; }
            while (taken.Contains(candidate));

            return candidate;
        }

        /// <summary>
        /// Everything that has to be true before a byte is written. Each refusal carries its own
        /// key: "it did nothing" is the one outcome a write must never produce.
        /// </summary>
        private static bool Precheck(string archivePath, ArchiveWriteResult result)
        {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
            {
                result.ErrorKey = "Str_Status_ArchiveWriteFailed";
                result.ErrorDetail = archivePath;
                return false;
            }

            if (!CanWrite(archivePath))
            {
                result.ErrorKey = "Str_Status_ArchiveReadOnly";
                return false;
            }

            try
            {
                if ((File.GetAttributes(archivePath) & FileAttributes.ReadOnly) != 0)
                {
                    result.ErrorKey = "Str_Status_ArchiveFileReadOnly";
                    return false;
                }
            }
            catch (Exception ex)
            {
                result.ErrorKey = "Str_Status_ArchiveWriteFailed";
                result.ErrorDetail = ex.Message;
                return false;
            }

            // Opened exclusively and closed again straight away. This is a check, not a lock -
            // nothing here can hold the file for the length of the rebuild - but it turns "the
            // archive is open in another program" into a refusal BEFORE any work, rather than a
            // failure at the swap after a multi-GB recompression.
            try
            {
                using var probe = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite,
                                                 FileShare.None);
            }
            catch (UnauthorizedAccessException)
            {
                result.ErrorKey = "Str_Status_ArchiveDenied";
                return false;
            }
            catch (IOException)
            {
                result.ErrorKey = "Str_Status_ArchiveInUse";
                return false;
            }

            // The rebuild writes its temp file into the archive's own folder, which is what makes
            // the swap a rename on the same volume. A folder that cannot be written to has to
            // fail here rather than after the work.
            string dir = Path.GetDirectoryName(archivePath) ?? ".";
            string probePath = Path.Combine(dir, Path.GetFileName(archivePath) + TempMark + "probe.tmp");
            try
            {
                using (var f = new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.None)) { }
                TryDelete(probePath);
            }
            catch (UnauthorizedAccessException)
            {
                result.ErrorKey = "Str_Status_ArchiveDenied";
                return false;
            }
            catch (Exception ex)
            {
                TryDelete(probePath);
                result.ErrorKey = "Str_Status_ArchiveWriteFailed";
                result.ErrorDetail = ex.Message;
                return false;
            }

            return true;
        }
    }
}
