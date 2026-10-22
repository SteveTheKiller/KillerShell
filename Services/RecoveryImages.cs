using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KillerShell.Services
{
    /// <summary>Durable publication receipts, bound to the exact file object and its bytes.</summary>
    internal static class RecoveryImages
    {
        private const string Marker = ".recovery-";
        private const uint Magic = 0x4B535231;

        internal sealed class CreatedImage
        {
            internal string Path = string.Empty;
            internal Identity Identity;
            internal byte[] Hash = Array.Empty<byte>();

            internal void Remove()
            {
                if (!File.Exists(Path) && !Directory.Exists(Path)) return;
                WithFile(Path, delete: true, stream =>
                {
                    Check(stream, Identity, Hash, Path);
                    Delete(stream, Path);
                });
            }
        }

        internal static CreatedImage WriteStaging(string path, byte[] data)
        {
            using var sha = SHA256.Create();
            byte[] expected = sha.ComputeHash(data);
            return WithFile(path, delete: true, stream =>
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
                if (!Equal(Hash(stream), expected)) throw new IOException("Replacement verification failed.");
                return new CreatedImage { Path = path, Identity = Identify(stream), Hash = expected };
            }, createNew: true);
        }

        internal sealed class Receipt
        {
            internal string Record = string.Empty, Image = string.Empty;
            internal bool Backup;
            internal Identity ImageIdentity, RecordIdentity;
            internal byte[] ImageHash = Array.Empty<byte>(), RecordHash = Array.Empty<byte>();

            internal void Verify()
            {
                VerifyFile(Record, RecordIdentity, RecordHash);
                if (File.Exists(Image) || Directory.Exists(Image)) VerifyFile(Image, ImageIdentity, ImageHash);
            }

            internal void Remove()
            {
                // Hold the receipt against modification while acting on its exact image.
                WithFile(Record, delete: true, record =>
                {
                    Check(record, RecordIdentity, RecordHash, Record);
                    if (File.Exists(Image) || Directory.Exists(Image))
                        WithFile(Image, delete: true, image =>
                        {
                            Check(image, ImageIdentity, ImageHash, Image);
                            Delete(image, Image);
                        });
                    Delete(record, Record);
                });
            }
        }

        internal static void Record(string target, string recovery, string existingImage, bool backup,
            CreatedImage? expected = null)
        {
            target = Path.GetFullPath(target);
            recovery = Path.GetFullPath(recovery);
            RejectLinks(target);
            RejectLinks(recovery);
            WithFile(existingImage, delete: false, image =>
            {
                Identity identity = Identify(image);
                byte[] hash = Hash(image);
                if (expected != null && (!identity.Equals(expected.Identity) || !Equal(hash, expected.Hash)))
                    throw new IOException("The staging image changed. It was preserved: " + existingImage);
                string recordPath = target + Marker + Guid.NewGuid().ToString("N");
                WithFile(recordPath, delete: true, record =>
                {
                    using var writer = new BinaryWriter(record, Encoding.UTF8, leaveOpen: true);
                    writer.Write(Magic);
                    WriteIdentity(writer, Identify(record));
                    writer.Write(target);
                    writer.Write(Path.GetFileName(recovery));
                    writer.Write(backup);
                    WriteIdentity(writer, identity);
                    writer.Write(hash);
                    writer.Flush();
                    record.Flush(true);
                }, createNew: true);
            });
        }

        internal static List<Receipt> Read(string target)
        {
            target = Path.GetFullPath(target);
            RejectLinks(target);
            string directory = Path.GetDirectoryName(target)!;
            var result = new List<Receipt>();
            if (!Directory.Exists(directory)) return result;
            foreach (string path in Directory.EnumerateFiles(directory, Path.GetFileName(target) + Marker + "*"))
            {
                // The name only locates candidate receipts. It never authorizes deletion.
                try
                {
                    Receipt? receipt = WithFile<Receipt?>(path, delete: false, stream =>
                    {
                        if (stream.Length > 4096) return null;
                        Identity recordIdentity = Identify(stream);
                        byte[] recordHash = Hash(stream);
                        stream.Position = 0;
                        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                        if (reader.ReadUInt32() != Magic || !ReadIdentity(reader).Equals(recordIdentity)
                            || !string.Equals(reader.ReadString(), target, StringComparison.OrdinalIgnoreCase)) return null;
                        string name = reader.ReadString();
                        bool backup = reader.ReadBoolean();
                        string prefix = Path.GetFileName(target) + (backup ? VerifiedFile.OldMark : ".new-");
                        if (!name.StartsWith(prefix, StringComparison.Ordinal)
                            || !Guid.TryParseExact(name.Substring(prefix.Length), "N", out _)) return null;
                        Identity imageIdentity = ReadIdentity(reader);
                        byte[] imageHash = reader.ReadBytes(32);
                        if (imageHash.Length != 32 || stream.Position != stream.Length) return null;
                        return new Receipt
                        {
                            Record = path, Image = Path.Combine(directory, name), Backup = backup,
                            RecordIdentity = recordIdentity, RecordHash = recordHash,
                            ImageIdentity = imageIdentity, ImageHash = imageHash,
                        };
                    });
                    if (receipt != null) result.Add(receipt);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            return result;
        }

        internal static void Cleanup(string target, bool includeUnpublished)
        {
            foreach (var receipt in Read(target))
            {
                if (!includeUnpublished && !receipt.Backup && File.Exists(receipt.Image)) continue;
                try { receipt.Remove(); }
                catch (Exception error)
                {
                    throw new IOException("Recovery cleanup failed. Recovery image: " + receipt.Image
                        + ". Ownership record: " + receipt.Record + ". " + error.Message, error);
                }
            }
        }

        private static void VerifyFile(string path, Identity identity, byte[] hash)
            => WithFile(path, delete: false, stream => Check(stream, identity, hash, path));

        private static void Check(FileStream stream, Identity identity, byte[] hash, string path)
        {
            if (!Identify(stream).Equals(identity) || !Equal(Hash(stream), hash))
                throw new IOException("A recovery file changed. It was preserved: " + path);
        }

        private static bool Equal(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
            return true;
        }

        private static void WithFile(string path, bool delete, Action<FileStream> action, bool createNew = false)
            => WithFile(path, delete, stream => { action(stream); return true; }, createNew);

        private static T WithFile<T>(string path, bool delete, Func<FileStream, T> action, bool createNew = false)
        {
            RejectLinks(path);
            // A deletion handle denies competing writes and renames. Disposition then applies
            // to the checked object even if an ancestor name changes after opening it.
            using SafeFileHandle handle = CreateFile(path,
                0x80000000u | (delete ? 0x00010000u : 0u) | (createNew ? 0x40000000u : 0u),
                createNew ? 0u : delete ? 1u : 5u, IntPtr.Zero, createNew ? 1u : 3u, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Cannot open recovery file: " + path, new Win32Exception(error));
            }
            using var stream = new FileStream(handle, createNew ? FileAccess.ReadWrite : FileAccess.Read);
            try
            {
                _ = Identify(stream);
                return action(stream);
            }
            catch (Exception error)
            {
                if (createNew)
                {
                    // This exclusive creation handle still owns even a partial write. Remove
                    // it before releasing the handle, without trusting the path again.
                    try { Delete(stream, path); }
                    catch (Exception cleanup)
                    {
                        throw new IOException("Incomplete publication file cleanup failed: " + path,
                            new AggregateException(error, cleanup));
                    }
                }
                throw;
            }
        }

        private static void Delete(FileStream stream, string path)
        {
            var disposition = new Disposition { DeleteFile = true };
            if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref disposition, 1))
                throw new IOException("Cannot delete recovery file: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        }

        private static byte[] Hash(FileStream stream)
        {
            stream.Position = 0;
            using var sha = SHA256.Create();
            return sha.ComputeHash(stream);
        }

        private static Identity Identify(FileStream stream)
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
                throw new IOException("Cannot identify recovery file.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((information.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || information.Links != 1)
                throw new IOException("Recovery ownership cannot be established for a linked file.");
            return new Identity
            {
                Volume = information.Volume, High = information.IndexHigh, Low = information.IndexLow,
                CreatedHigh = information.CreatedHigh, CreatedLow = information.CreatedLow,
            };
        }

        internal static void RejectLinks(string path)
        {
            for (string? item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item))
                    && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Recovery cannot traverse a linked path: " + item);
        }

        internal struct Identity : IEquatable<Identity>
        {
            internal uint Volume, High, Low, CreatedHigh, CreatedLow;
            public bool Equals(Identity other) => Volume == other.Volume && High == other.High && Low == other.Low
                && CreatedHigh == other.CreatedHigh && CreatedLow == other.CreatedLow;
        }

        private static void WriteIdentity(BinaryWriter writer, Identity identity)
        {
            writer.Write(identity.Volume); writer.Write(identity.High); writer.Write(identity.Low);
            writer.Write(identity.CreatedHigh); writer.Write(identity.CreatedLow);
        }

        private static Identity ReadIdentity(BinaryReader reader) => new Identity
        {
            Volume = reader.ReadUInt32(), High = reader.ReadUInt32(), Low = reader.ReadUInt32(),
            CreatedHigh = reader.ReadUInt32(), CreatedLow = reader.ReadUInt32(),
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Disposition
        {
            [MarshalAs(UnmanagedType.U1)] internal bool DeleteFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
            ref Disposition information, uint size);
    }
}
