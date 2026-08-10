using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// Reading INSIDE an archive as if it were a folder. Pure: no WPF, no window, no UI state -
// paths in, entries out - so the listing, the tree and any future test can all drive it.
//
// FORMATS AND WHY THERE ARE NO NEW DEPENDENCIES:
//   .zip           ZipArchive, part of .NET Framework itself (System.IO.Compression).
//   .tar           parsed here - a tar is 512-byte headers between 512-byte-aligned blobs.
//   .tar.gz .tgz   GZipStream (also in the framework) feeding the same tar reader.
//   .gz            a single gzip-wrapped file, so it lists as one entry.
// KillerShell ships as a single exe with zero runtime dependencies, and that stays true:
// nothing here needs a package. RAR and 7z are deliberately NOT supported - RAR is
// proprietary and unrar's licence forbids reimplementing it, so both would mean taking a
// dependency (and then embedding it to keep the one-file promise). They report
// ArchiveSupport.Unsupported rather than pretending.
//
// The model is a FLAT entry list plus a virtual path, not a nested tree: every archive format
// stores a flat list of full-path entries, and folders inside one are usually implied by those
// paths rather than stored. Building a tree up front would mean inventing nodes for folders
// that were never written; deriving one level at a time from the flat list is both simpler and
// exactly what the browse UI asks for.
namespace KillerShell.Services
{
    /// <summary>What this build can do with a given archive.</summary>
    internal enum ArchiveSupport
    {
        /// <summary>Not an archive path at all.</summary>
        None,
        /// <summary>Can be browsed and extracted.</summary>
        Read,
        /// <summary>A real archive, but this build cannot open it (rar, 7z).</summary>
        Unsupported,
    }

    /// <summary>One entry inside an archive, at the level being listed.</summary>
    internal sealed class ArchiveEntryInfo
    {
        /// <summary>Name at this level, no path.</summary>
        internal string Name = "";
        /// <summary>Full path INSIDE the archive, forward-slash separated, no leading slash.
        /// Empty for the archive root.</summary>
        internal string EntryPath = "";
        internal bool IsDirectory;
        /// <summary>Uncompressed bytes. For a folder, the sum of everything beneath it.</summary>
        internal long Size;
        /// <summary>Last write time as recorded in the archive, or default when it carries none.</summary>
        internal DateTime Modified;
    }

    internal static class ArchiveProvider
    {
        // Kept as a set rather than a switch so the two callers that need "is this openable"
        // and "is this a known archive we refuse" cannot drift apart.
        private static readonly HashSet<string> ReadableExts = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".tar", ".gz", ".tgz" };

        private static readonly HashSet<string> KnownUnsupportedExts = new(StringComparer.OrdinalIgnoreCase)
        { ".rar", ".7z", ".bz2", ".xz", ".cab", ".iso" };

        /// <summary>
        /// What can be done with this file. Extension-based on purpose: sniffing magic bytes
        /// means opening every file the listing draws, and a listing draws thousands.
        /// </summary>
        internal static ArchiveSupport Classify(string? path)
        {
            if (string.IsNullOrEmpty(path)) return ArchiveSupport.None;

            // ".tar.gz" is two extensions; GetExtension only ever sees the last one.
            if (path!.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ArchiveSupport.Read;

            string ext = Path.GetExtension(path);
            if (ReadableExts.Contains(ext)) return ArchiveSupport.Read;
            if (KnownUnsupportedExts.Contains(ext)) return ArchiveSupport.Unsupported;
            return ArchiveSupport.None;
        }

        internal static bool IsReadable(string? path) => Classify(path) == ArchiveSupport.Read;

        // ── The virtual path ─────────────────────────────────────
        // A location inside an archive is written as the archive's own path, then the separator,
        // then the entry path: "C:\x\src.zip?docs/img". One string carries the whole location,
        // which is what lets a tab, a bookmark and the address bar keep treating a location as a
        // single string.
        //
        // THE SEPARATOR HAS TO SATISFY TWO OPPOSING RULES, and getting it wrong is not subtle:
        //   1. It must be illegal in a real FILENAME, or a virtual path could collide with an
        //      actual file on disk.
        //   2. It must be LEGAL as far as System.IO.Path is concerned, because row paths flow
        //      into Path.GetExtension and friends all over the app.
        // '>' satisfies (1) and FAILS (2): on .NET Framework - which this app targets - '>' is
        // in Path.GetInvalidPathChars(), so Path.GetExtension THROWS ArgumentException, "Illegal
        // characters in path". That took out the details-pane preview and silently killed every
        // icon inside an archive, because IconCache catches the throw and falls back to no
        // extension (2026-08-09, first build of this feature).
        // '?' satisfies BOTH: Windows forbids it in a filename, but it is NOT in
        // GetInvalidPathChars, so Path parsing accepts it. Better still, the entry path's '/'
        // separators are real directory separators to Windows, so Path.GetExtension on a
        // virtual path returns the ENTRY's extension, which is exactly what the icon lookup and
        // the preview test want.
        // Do NOT "tidy" this back to '>' or '|' - both are invalid path chars and both bring
        // the crash back.
        internal const char ArchiveSep = '?';

        /// <summary>Splits a virtual path into the archive file and the path inside it. Returns
        /// false when this is an ordinary filesystem path.</summary>
        internal static bool TrySplit(string? virtualPath, out string archivePath, out string entryPath)
        {
            archivePath = ""; entryPath = "";
            if (string.IsNullOrEmpty(virtualPath)) return false;

            int i = virtualPath!.IndexOf(ArchiveSep);
            if (i < 0)
            {
                // No separator: the path may still BE an archive file, browsed at its root.
                if (!IsReadable(virtualPath)) return false;
                archivePath = virtualPath;
                return true;
            }

            archivePath = virtualPath.Substring(0, i);
            entryPath = virtualPath.Substring(i + 1).Trim('/');
            return IsReadable(archivePath);
        }

        internal static string Combine(string archivePath, string entryPath)
            => entryPath.Length == 0 ? archivePath : archivePath + ArchiveSep + entryPath.Trim('/');

        /// <summary>The virtual path one level up, or null when already at the archive root
        /// (where "up" means the archive file's own folder, which is the caller's business).</summary>
        internal static string? Parent(string virtualPath)
        {
            if (!TrySplit(virtualPath, out string archive, out string entry) || entry.Length == 0) return null;
            int slash = entry.LastIndexOf('/');
            return slash < 0 ? archive : Combine(archive, entry.Substring(0, slash));
        }

        // ── Listing ──────────────────────────────────────────────
        /// <summary>
        /// One level of an archive: the entries directly under <paramref name="entryPath"/>,
        /// with implied folders synthesized from deeper entries and their sizes rolled up.
        /// </summary>
        /// <remarks>
        /// Throws nothing for a bad archive - callers get an empty list and the reason in
        /// <paramref name="error"/>, because a corrupt zip in a folder listing must not take
        /// the pane down with it.
        /// </remarks>
        internal static List<ArchiveEntryInfo> List(string archivePath, string entryPath, out string? error)
        {
            error = null;
            var result = new List<ArchiveEntryInfo>();
            try
            {
                string prefix = entryPath.Length == 0 ? "" : entryPath.Trim('/') + "/";

                // Folder name -> rolled-up size and newest timestamp, so a folder row can show
                // what it holds rather than a blank.
                var dirs = new Dictionary<string, ArchiveEntryInfo>(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in ReadEntries(archivePath))
                {
                    string full = raw.EntryPath;
                    if (prefix.Length > 0)
                    {
                        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        full = full.Substring(prefix.Length);
                    }
                    if (full.Length == 0) continue;

                    int slash = full.IndexOf('/');
                    if (slash < 0)
                    {
                        // Sits directly at this level. A trailing-slash entry arrives here with
                        // its slash already trimmed by ReadEntries and IsDirectory set.
                        if (raw.IsDirectory)
                        {
                            if (!dirs.ContainsKey(full))
                                dirs[full] = new ArchiveEntryInfo
                                { Name = full, EntryPath = prefix + full, IsDirectory = true, Modified = raw.Modified };
                        }
                        else result.Add(new ArchiveEntryInfo
                        {
                            Name = full, EntryPath = prefix + full,
                            Size = raw.Size, Modified = raw.Modified,
                        });
                        continue;
                    }

                    // Deeper: the first segment is a folder at THIS level, whether or not the
                    // archive bothered to store an entry for it. Most zips do not.
                    string dirName = full.Substring(0, slash);
                    if (!dirs.TryGetValue(dirName, out var d))
                    {
                        d = new ArchiveEntryInfo
                        { Name = dirName, EntryPath = prefix + dirName, IsDirectory = true };
                        dirs[dirName] = d;
                    }
                    d.Size += raw.Size;
                    if (raw.Modified > d.Modified) d.Modified = raw.Modified;
                }

                result.AddRange(dirs.Values);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return result;
        }

        /// <summary>Every entry in the archive, flat, paths normalized to forward slashes with
        /// no leading slash. Directory entries keep IsDirectory and lose their trailing slash.</summary>
        private static IEnumerable<ArchiveEntryInfo> ReadEntries(string archivePath)
        {
            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                return ReadTar(archivePath, gzip: true);

            string ext = Path.GetExtension(archivePath);
            if (ext.Equals(".tar", StringComparison.OrdinalIgnoreCase)) return ReadTar(archivePath, gzip: false);
            if (ext.Equals(".gz", StringComparison.OrdinalIgnoreCase))  return ReadSingleGzip(archivePath);
            return ReadZip(archivePath);
        }

        private static IEnumerable<ArchiveEntryInfo> ReadZip(string archivePath)
        {
            var list = new List<ArchiveEntryInfo>();
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var e in zip.Entries)
            {
                string p = Normalize(e.FullName);
                if (p.Length == 0) continue;

                // A zip marks a folder by ending the name in '/' AND having no content. Both
                // halves matter: some writers store a zero-length FILE with a normal name.
                bool isDir = e.FullName.EndsWith("/", StringComparison.Ordinal);
                list.Add(new ArchiveEntryInfo
                {
                    EntryPath = p,
                    IsDirectory = isDir,
                    Size = isDir ? 0 : e.Length,
                    // DateTimeOffset because a zip stores local DOS time with no zone; taking
                    // .DateTime keeps what was written rather than shifting it.
                    Modified = SafeTime(e),
                });
            }
            return list;
        }

        private static DateTime SafeTime(ZipArchiveEntry e)
        {
            // A zip with a zeroed or out-of-range DOS timestamp throws from LastWriteTime
            // rather than returning a default, and one bad entry must not fail the listing.
            try { return e.LastWriteTime.DateTime; }
            catch { return default; }
        }

        /// <summary>A lone .gz holds exactly one file and does not record its name, so the
        /// convention is the archive's own name minus the .gz.</summary>
        private static IEnumerable<ArchiveEntryInfo> ReadSingleGzip(string archivePath)
        {
            string inner = Path.GetFileNameWithoutExtension(archivePath);
            long size = 0;
            try
            {
                // gzip stores the uncompressed size mod 2^32 in the last four bytes. That is
                // exact for anything under 4GB and the only way to know without inflating the
                // whole stream, which a folder listing must never do.
                using var fs = File.OpenRead(archivePath);
                if (fs.Length >= 4)
                {
                    fs.Seek(-4, SeekOrigin.End);
                    var b = new byte[4];
                    if (fs.Read(b, 0, 4) == 4) size = BitConverter.ToUInt32(b, 0);
                }
            }
            catch { /* unreadable tail - the row just shows no size */ }

            return
            [
                new ArchiveEntryInfo
                {
                    Name = inner, EntryPath = Normalize(inner), Size = size,
                    Modified = SafeFileTime(archivePath),
                }
            ];
        }

        private static DateTime SafeFileTime(string path)
        {
            try { return File.GetLastWriteTime(path); } catch { return default; }
        }

        // ── tar ──────────────────────────────────────────────────
        // Written directly rather than taken from a package: System.Formats.Tar is .NET 7+ and
        // this app is net48, and the format is small enough that a reader is cheaper than a
        // dependency. Layout: a 512-byte header per member, then its content padded up to the
        // next 512-byte boundary. Two zeroed headers end the archive.
        private const int TarBlock = 512;

        private static IEnumerable<ArchiveEntryInfo> ReadTar(string archivePath, bool gzip)
        {
            var list = new List<ArchiveEntryInfo>();

            using FileStream file = File.OpenRead(archivePath);
            using Stream s = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;

            var header = new byte[TarBlock];
            // A GZipStream is not seekable, so skipping content means reading it away.
            var skip = new byte[81920];
            string longName = "";   // set by a GNU 'L' header for the entry that follows

            while (true)
            {
                if (!ReadExactly(s, header, TarBlock)) break;

                // Two consecutive zero blocks mark the end; one is enough to stop on.
                bool allZero = true;
                for (int i = 0; i < TarBlock && allZero; i++) if (header[i] != 0) allZero = false;
                if (allZero) break;

                string name = TarString(header, 0, 100);
                string prefix = TarString(header, 345, 155);   // ustar splits long names in two
                if (prefix.Length > 0) name = prefix + "/" + name;
                if (longName.Length > 0) { name = longName; longName = ""; }

                long size = TarOctal(header, 124, 12);
                char type = (char)header[156];
                long mtime = TarOctal(header, 136, 12);

                // A long name arrives one of two ways, and BOTH have to be handled - a reader
                // that only knows GNU silently truncates every long path written by anything
                // modern (caught by test, 2026-08-09):
                //   'L'  GNU long name - the record's content IS the next entry's name.
                //   'x'  pax extended header - the content is "path=..." among other records,
                //        and the ustar header that follows carries a TRUNCATED name. pax is
                //        the default for bsdtar, macOS tar and Python's tarfile, so this is
                //        the common case, not the exotic one.
                // 'g' is a pax GLOBAL header: its defaults apply to the whole archive, and a
                // path in one would not name the next entry, so its content is skipped.
                if (type == 'L' || type == 'x' || type == 'X' || type == 'g')
                {
                    var metaBuf = new byte[size];
                    if (!ReadExactly(s, metaBuf, (int)size)) break;
                    if (type == 'L') longName = Encoding.UTF8.GetString(metaBuf).TrimEnd('\0');
                    else if (type != 'g') longName = PaxPath(metaBuf) ?? longName;
                    SkipPadding(s, size, skip);
                    continue;
                }

                // '0'/'\0' regular file, '5' directory. Everything else (links, devices, the
                // pax 'x' records) is skipped rather than shown as a file it is not.
                bool isDir = type == '5' || name.EndsWith("/", StringComparison.Ordinal);
                bool isFile = type == '0' || type == '\0';

                if (isDir || isFile)
                {
                    string p = Normalize(name);
                    if (p.Length > 0)
                        list.Add(new ArchiveEntryInfo
                        {
                            EntryPath = p,
                            IsDirectory = isDir,
                            Size = isDir ? 0 : size,
                            Modified = mtime > 0
                                ? DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime
                                : default,
                        });
                }

                // Content always follows for a file record, whether or not it was listed.
                long remaining = size;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(skip.Length, remaining);
                    if (!ReadExactly(s, skip, want)) return list;
                    remaining -= want;
                }
                SkipPadding(s, size, skip);
            }
            return list;
        }

        private static void SkipPadding(Stream s, long size, byte[] scratch)
        {
            int pad = (int)(size % TarBlock);
            if (pad == 0) return;
            ReadExactly(s, scratch, TarBlock - pad);
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes, looping because a GZipStream
        /// returns short reads freely. False means the stream ended early.</summary>
        private static bool ReadExactly(Stream s, byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buffer, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        /// <summary>
        /// The "path" value out of a pax extended header, or null when it carries none.
        /// </summary>
        /// <remarks>
        /// A pax header is a run of records, each "&lt;length&gt; key=value\n" where length counts
        /// the WHOLE record including its own digits and the newline. Records are walked by
        /// that length rather than split on newlines, because a value is allowed to contain
        /// one. "path" wins over the truncated ustar name; everything else (mtime, uid, sizes
        /// past the octal limit) is ignored here - the listing does not use them.
        /// </remarks>
        private static string? PaxPath(byte[] content)
        {
            int i = 0;
            while (i < content.Length)
            {
                // Leading decimal length.
                int start = i;
                int len = 0;
                while (i < content.Length && content[i] >= '0' && content[i] <= '9')
                {
                    len = (len * 10) + (content[i] - '0');
                    i++;
                }
                if (len <= 0 || start + len > content.Length) return null;   // malformed - stop
                if (i >= content.Length || content[i] != ' ') return null;
                i++;   // the space after the length

                int keyStart = i;
                while (i < content.Length && content[i] != '=') i++;
                if (i >= content.Length) return null;

                string key = Encoding.UTF8.GetString(content, keyStart, i - keyStart);
                int valueStart = i + 1;
                int valueLen = (start + len) - valueStart - 1;   // minus the trailing newline
                if (valueLen < 0) return null;

                if (key == "path")
                    return Encoding.UTF8.GetString(content, valueStart, valueLen).TrimEnd('\0');

                i = start + len;   // next record
            }
            return null;
        }

        private static string TarString(byte[] b, int offset, int length)
        {
            int end = offset;
            int limit = offset + length;
            while (end < limit && b[end] != 0) end++;
            return Encoding.UTF8.GetString(b, offset, end - offset).Trim();
        }

        /// <summary>Tar numbers are octal ASCII. Some writers use a binary form flagged by the
        /// high bit, which is honored here rather than read as a garbage size.</summary>
        private static long TarOctal(byte[] b, int offset, int length)
        {
            if ((b[offset] & 0x80) != 0)
            {
                long big = 0;
                for (int i = offset + 1; i < offset + length; i++) big = (big << 8) | b[i];
                return big;
            }

            long value = 0;
            for (int i = offset; i < offset + length; i++)
            {
                byte c = b[i];
                if (c == 0 || c == ' ') { if (value > 0) break; else continue; }
                if (c < '0' || c > '7') break;
                value = (value * 8) + (c - '0');
            }
            return value;
        }

        // ── Extraction ───────────────────────────────────────────
        /// <summary>
        /// Extracts one entry to a temp file and returns its path, so an entry can be opened,
        /// previewed or copied out with ordinary file code. Returns null when the entry is not
        /// found or cannot be read.
        /// </summary>
        /// <remarks>
        /// The temp file keeps the entry's own name inside a per-extraction folder rather than
        /// getting a random one: the name is what the opening app shows in its title bar, and
        /// what its "save as" starts from. The folder makes two entries of the same name from
        /// different archives impossible to collide.
        /// </remarks>
        internal static string? ExtractToTemp(string archivePath, string entryPath, out string? error)
        {
            error = null;
            try
            {
                string safeName = Path.GetFileName(entryPath.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(safeName)) { error = "no file name"; return null; }

                string dir = Path.Combine(Path.GetTempPath(), "KillerShell", "archive",
                                          Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, safeName);

                if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                    || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                    || archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ExtractFromTar(archivePath, entryPath, dest)) { error = "entry not found"; return null; }
                    return dest;
                }

                if (archivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    using var fs = File.OpenRead(archivePath);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    using var outFs = File.Create(dest);
                    gz.CopyTo(outFs);
                    return dest;
                }

                using (var zip = ZipFile.OpenRead(archivePath))
                {
                    var entry = zip.Entries.FirstOrDefault(
                        x => string.Equals(Normalize(x.FullName), entryPath.Trim('/'), StringComparison.OrdinalIgnoreCase));
                    if (entry == null) { error = "entry not found"; return null; }
                    entry.ExtractToFile(dest, overwrite: true);
                }
                return dest;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static bool ExtractFromTar(string archivePath, string entryPath, string dest)
        {
            string want = entryPath.Trim('/');
            using FileStream file = File.OpenRead(archivePath);
            using Stream s = archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                ? file
                : new GZipStream(file, CompressionMode.Decompress);

            var header = new byte[TarBlock];
            var buf = new byte[81920];
            string longName = "";

            while (ReadExactly(s, header, TarBlock))
            {
                bool allZero = true;
                for (int i = 0; i < TarBlock && allZero; i++) if (header[i] != 0) allZero = false;
                if (allZero) return false;

                string name = TarString(header, 0, 100);
                string prefix = TarString(header, 345, 155);
                if (prefix.Length > 0) name = prefix + "/" + name;
                if (longName.Length > 0) { name = longName; longName = ""; }

                long size = TarOctal(header, 124, 12);
                char type = (char)header[156];

                if (type == 'L' || type == 'x' || type == 'X' || type == 'g')
                {
                    var metaBuf = new byte[size];
                    if (!ReadExactly(s, metaBuf, (int)size)) return false;
                    if (type == 'L') longName = Encoding.UTF8.GetString(metaBuf).TrimEnd('\0');
                    else if (type != 'g') longName = PaxPath(metaBuf) ?? longName;
                    SkipPadding(s, size, buf);
                    continue;
                }

                bool match = string.Equals(Normalize(name), want, StringComparison.OrdinalIgnoreCase)
                             && (type == '0' || type == '\0');

                if (match)
                {
                    using var outFs = File.Create(dest);
                    long remaining = size;
                    while (remaining > 0)
                    {
                        int n = (int)Math.Min(buf.Length, remaining);
                        if (!ReadExactly(s, buf, n)) return false;
                        outFs.Write(buf, 0, n);
                        remaining -= n;
                    }
                    return true;
                }

                long skipLeft = size;
                while (skipLeft > 0)
                {
                    int n = (int)Math.Min(buf.Length, skipLeft);
                    if (!ReadExactly(s, buf, n)) return false;
                    skipLeft -= n;
                }
                SkipPadding(s, size, buf);
            }
            return false;
        }

        // ── Path hygiene ─────────────────────────────────────────
        /// <summary>
        /// Archive-internal path: forward slashes, no leading or trailing slash, no "." or ".."
        /// segments.
        /// </summary>
        /// <remarks>
        /// Dropping ".." is a security measure, not tidiness. A crafted archive with entries
        /// like "../../windows/system32/x.dll" is the Zip Slip attack: extract that name
        /// relative to a destination folder and it writes OUTSIDE it. Normalizing here means
        /// no caller can be handed a traversing path in the first place, and ExtractToTemp
        /// takes only the leaf name on top of that.
        /// </remarks>
        private static string Normalize(string entryName)
        {
            string p = entryName.Replace('\\', '/').Trim('/');
            if (p.Length == 0) return "";
            if (p.IndexOf("..", StringComparison.Ordinal) < 0 && p.IndexOf("./", StringComparison.Ordinal) < 0)
                return p;

            var parts = p.Split('/')
                         .Where(seg => seg.Length > 0 && seg != "." && seg != "..");
            return string.Join("/", parts);
        }
    }
}
