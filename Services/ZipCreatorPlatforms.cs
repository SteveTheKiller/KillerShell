using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace KillerShell.Services
{
    // .NET Framework exposes external attributes but not the platform that interprets
    // them. Preserve that byte in the staged archive before publication. Record layout:
    // https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT, sections 4.3.12-16.
    internal static class ZipCreatorPlatforms
    {
        internal static byte[] Read(Stream stream, int entryCount, CancellationToken ct)
        {
            var platforms = new byte[entryCount];
            VisitDirectory(stream, entryCount, ct, (index, position, platform) => platforms[index] = platform);
            return platforms;
        }

        internal static void Restore(string path, List<byte> retained, int entryCount, CancellationToken ct)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            VisitDirectory(stream, entryCount, ct, (index, position, platform) =>
            {
                // Retained entries precede filesystem additions, which keep their native platform.
                if (index >= retained.Count) return;
                stream.Position = position;
                stream.WriteByte(retained[index]);
            });
        }

        private static void VisitDirectory(Stream stream, int expected, CancellationToken ct,
                                           Action<int, long, byte> visit)
        {
            ct.ThrowIfCancellationRequested();
            int tailLength = (int)Math.Min(stream.Length, ushort.MaxValue + 22L);
            byte[] tail = ReadBlock(stream, stream.Length - tailLength, tailLength);
            int end = tailLength - 22;
            for (; end >= 0; end--)
                if (U32(tail, end) == 0x06054b50 && end + 22 + U16(tail, end + 20) == tailLength)
                    break;
            if (end < 0) throw InvalidDirectory();

            long endPosition = stream.Length - tailLength + end;
            if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0)
                throw InvalidDirectory();
            long count = U16(tail, end + 10);
            long size = U32(tail, end + 12);
            long offset = U32(tail, end + 16);
            long directoryLimit = endPosition;
            bool needsZip64 = count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue;
            byte[]? locator = endPosition >= 20 ? ReadBlock(stream, endPosition - 20, 20) : null;
            if (locator != null && U32(locator, 0) == 0x07064b50)
            {
                if (U32(locator, 4) != 0 || U32(locator, 16) != 1) throw InvalidDirectory();
                long zip64Offset = checked((long)BitConverter.ToUInt64(locator, 8));
                byte[] record = ReadBlock(stream, zip64Offset, 56);
                long recordSize = checked((long)BitConverter.ToUInt64(record, 4));
                if (U32(record, 0) != 0x06064b50 || recordSize < 44 ||
                    zip64Offset > endPosition - 32 || recordSize != endPosition - 32 - zip64Offset ||
                    U32(record, 16) != 0 || U32(record, 20) != 0)
                    throw InvalidDirectory();
                count = checked((long)BitConverter.ToUInt64(record, 32));
                if (BitConverter.ToUInt64(record, 24) != (ulong)count) throw InvalidDirectory();
                size = checked((long)BitConverter.ToUInt64(record, 40));
                offset = checked((long)BitConverter.ToUInt64(record, 48));
                directoryLimit = zip64Offset;
            }
            else if (needsZip64 || U16(tail, end + 8) != count)
                throw InvalidDirectory();

            if (count != expected || size < count * 46 || offset > directoryLimit || size > directoryLimit - offset)
                throw InvalidDirectory();
            long directoryEnd = offset + size;
            for (int index = 0; index < expected; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (directoryEnd - offset < 46) throw InvalidDirectory();
                byte[] header = ReadBlock(stream, offset, 46);
                if (U32(header, 0) != 0x02014b50) throw InvalidDirectory();
                int variableLength = U16(header, 28) + U16(header, 30) + U16(header, 32);
                if (variableLength > directoryEnd - offset - 46) throw InvalidDirectory();
                visit(index, offset + 5, header[5]);
                offset += 46 + variableLength;
            }
            // A central-directory digital signature may follow the entries.
            if (offset != directoryEnd)
            {
                if (directoryEnd - offset < 6) throw InvalidDirectory();
                byte[] signature = ReadBlock(stream, offset, 6);
                if (U32(signature, 0) != 0x05054b50 || U16(signature, 4) != directoryEnd - offset - 6)
                    throw InvalidDirectory();
            }
        }

        private static byte[] ReadBlock(Stream stream, long position, int count)
        {
            if (position < 0 || position > stream.Length || count > stream.Length - position)
                throw InvalidDirectory();
            stream.Position = position;
            var bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int next = stream.Read(bytes, read, count - read);
                if (next == 0) throw InvalidDirectory();
                read += next;
            }
            return bytes;
        }

        private static ushort U16(byte[] bytes, int offset) => BitConverter.ToUInt16(bytes, offset);
        private static uint U32(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset);
        private static InvalidDataException InvalidDirectory()
            => new InvalidDataException("Cannot safely preserve ZIP creator-platform metadata.");
    }
}
