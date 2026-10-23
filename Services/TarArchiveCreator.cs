using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace KillerShell.Services
{
    internal static class TarArchiveCreator
    {
        private const int Block = 512;
        private const long OctalSizeLimit = 8589934591L;
        private static readonly byte[] Padding = new byte[Block];
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        // New archives only. Existing TAR files retain their read-only rewrite policy.
        internal static ArchiveWriteResult Create(string destination, IEnumerable<string> sources, bool gzip,
            Action<int, int, string>? progress, CancellationToken cancellationToken)
        {
            var result = new ArchiveWriteResult();
            string? temporary = null;
            bool ownsTemporary = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination = Path.GetFullPath(destination);
                string parent = Path.GetDirectoryName(destination) ?? throw new IOException("The destination has no parent folder.");
                if (FirstLink(parent) != null) throw new IOException("The destination folder contains a link.");
                if (File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException("The destination already exists.");
                var entries = Collect(sources, result, cancellationToken);
                if (entries.Count == 0)
                {
                    result.ErrorKey = "Str_Status_ArchiveNothingAdded";
                    return result;
                }
                temporary = destination + ".kscreate-" + Guid.NewGuid().ToString("N") + (gzip ? ".tar.gz" : ".tar");
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    if (gzip)
                    {
                        using var compressed = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
                        Write(compressed, entries, progress, cancellationToken);
                    }
                    else Write(output, entries, progress, cancellationToken);
                    output.Flush(true);
                }
                ArchiveProvider.List(temporary, "", out string? error, cancellationToken);
                if (error != null) throw new InvalidDataException(error);
                cancellationToken.ThrowIfCancellationRequested();
                if (FirstLink(parent) != null) throw new IOException("The destination folder became a link.");
                File.Move(temporary, destination);
                ownsTemporary = false;
                result.Ok = true;
                result.Changed = entries.Count;
            }
            catch (OperationCanceledException) { result.ErrorKey = "Str_Status_ArchiveCanceled"; }
            catch (Exception ex)
            {
                result.ErrorKey = "Str_Status_ArchiveWriteFailed";
                result.ErrorDetail = ex.Message;
            }
            finally
            {
                if (ownsTemporary && temporary != null)
                    try { File.Delete(temporary); } catch { }
            }
            return result;
        }

        private sealed class Entry
        {
            internal string Source = "";
            internal string Name = "";
            internal bool Directory;
            internal long Modified;
        }

        private static List<Entry> Collect(IEnumerable<string> sources, ArchiveWriteResult result, CancellationToken ct)
        {
            var entries = new List<Entry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<(string Source, string Name)>();
            foreach (string source in sources.Reverse())
            {
                string full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
                string name = Path.GetFileName(full);
                if (!ArchiveWriter.IsLegalLeafName(name)) throw new IOException("A source has no valid archive name.");
                pending.Push((full, name));
            }
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var next = pending.Pop();
                var link = FirstLink(next.Source);
                if (link != null)
                {
                    if ((link.Value & FileAttributes.Directory) != 0) result.SkippedDirectoryLinks++;
                    else result.Rejected.Add(next.Source);
                    continue;
                }
                bool directory = (File.GetAttributes(next.Source) & FileAttributes.Directory) != 0;
                if (ArchiveWriter.SafeEntryName(next.Name) != next.Name || !names.Add(next.Name))
                    throw new IOException("Source names overlap or cannot be stored safely.");
                entries.Add(new Entry
                {
                    Source = next.Source, Name = next.Name, Directory = directory,
                    Modified = Math.Max(0, new DateTimeOffset(File.GetLastWriteTimeUtc(next.Source)).ToUnixTimeSeconds()),
                });
                if (directory)
                    foreach (string child in Directory.EnumerateFileSystemEntries(next.Source)
                        .OrderByDescending(path => path, StringComparer.Ordinal))
                        pending.Push((child, next.Name + "/" + Path.GetFileName(child)));
            }
            return entries;
        }

        private static FileAttributes? FirstLink(string path)
        {
            var parents = new Stack<string>();
            for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                parents.Push(current!);
            while (parents.Count > 0)
            {
                var attributes = File.GetAttributes(parents.Pop());
                if ((attributes & FileAttributes.ReparsePoint) != 0) return attributes;
            }
            return null;
        }

        private static void Write(Stream output, List<Entry> entries, Action<int, int, string>? progress, CancellationToken ct)
        {
            var buffer = new byte[81920];
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                if (FirstLink(entry.Source) != null)
                    throw new IOException("A source became a link during archive creation.");
                using var input = entry.Directory ? null : new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                long size = input?.Length ?? 0;
                string name = entry.Name + (entry.Directory ? "/" : "");
                string stamp = "entry-" + i.ToString("D8", CultureInfo.InvariantCulture);
                byte[] metadata = Utf8.GetBytes(PaxRecord("path", name)
                    + (size > OctalSizeLimit ? PaxRecord("size", size.ToString(CultureInfo.InvariantCulture)) : ""));
                WriteHeader(output, "PaxHeaders/" + stamp, metadata.Length, entry.Modified, 'x');
                output.Write(metadata, 0, metadata.Length);
                Pad(output, metadata.Length);
                WriteHeader(output, stamp, size <= OctalSizeLimit ? size : 0, entry.Modified, entry.Directory ? '5' : '0');
                long remaining = size;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = input!.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) throw new EndOfStreamException("A source changed during archive creation.");
                    output.Write(buffer, 0, count);
                    remaining -= count;
                }
                Pad(output, size);
                progress?.Invoke(i + 1, entries.Count, entry.Name);
            }
            output.Write(Padding, 0, Block);
            output.Write(Padding, 0, Block);
        }

        private static string PaxRecord(string key, string value)
        {
            string body = key + "=" + value + "\n";
            int bytes = Utf8.GetByteCount(body);
            int length = bytes + 2;
            while (true)
            {
                int actual = bytes + length.ToString(CultureInfo.InvariantCulture).Length + 1;
                if (actual == length) return length.ToString(CultureInfo.InvariantCulture) + " " + body;
                length = actual;
            }
        }

        private static void WriteHeader(Stream output, string name, long size, long modified, char type)
        {
            var header = new byte[Block];
            PutText(header, 0, name);
            PutOctal(header, 100, 8, type == '5' ? 493 : 420);
            PutOctal(header, 108, 8, 0);
            PutOctal(header, 116, 8, 0);
            PutOctal(header, 124, 12, size);
            PutOctal(header, 136, 12, modified);
            for (int i = 148; i < 156; i++) header[i] = (byte)' ';
            header[156] = (byte)type;
            PutText(header, 257, "ustar");
            PutText(header, 263, "00");
            PutText(header, 148, Convert.ToString(header.Sum(value => (int)value), 8).PadLeft(6, '0'));
            header[154] = 0;
            header[155] = (byte)' ';
            output.Write(header, 0, Block);
        }

        private static void PutOctal(byte[] header, int offset, int width, long value)
        {
            string text = Convert.ToString(value, 8);
            if (text.Length >= width) throw new IOException("Archive metadata exceeds its numeric field.");
            PutText(header, offset, text.PadLeft(width - 1, '0'));
        }

        private static void PutText(byte[] header, int offset, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            Array.Copy(bytes, 0, header, offset, bytes.Length);
        }

        private static void Pad(Stream output, long size)
        {
            int padding = (int)((Block - size % Block) % Block);
            if (padding > 0) output.Write(Padding, 0, padding);
        }
    }
}
