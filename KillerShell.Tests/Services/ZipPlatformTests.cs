using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ZipPlatformTests
    {
        // Python zipfile fixture with Unix file/directory modes, mixed creator platforms,
        // duplicate names, and a central-header signature inside its archive comment.
        private const string Fixture =
            "UEsDBBQAAAAAAAU5plgAAAAAAAAAAAAAAAAGAAAAdG9vbHMvUEsDBBQAAAAAAAU5plgIAQ5JDAAAAAwAAAAMAAAAdG9vbHMvcnVuLnNoZWNobyBleGFtcGxlUEsDBBQAAAAAAAU5plgwHYBoBgAAAAYAAAAKAAAAcmVtb3ZlLnR4dHJlbW92ZVBLAwQUAAAAAAAFOaZYV+5xkgUAAAAFAAAACAAAAHNhbWUudHh0Zmlyc3RQSwMEFAAAAAAABTmmWGkRH7YGAAAABgAAAAgAAABzYW1lLnR4dHNlY29uZFBLAQIUAxQAAAAAAAU5plgAAAAAAAAAAAAAAAAGAAAAAAAAAAAAEADtQQAAAAB0b29scy9QSwECFAMUAAAAAAAFOaZYCAEOSQwAAAAMAAAADAAAAAAAAAAAAAAA7YEkAAAAdG9vbHMvcnVuLnNoUEsBAhQAFAAAAAAABTmmWDAdgGgGAAAABgAAAAoAAAAAAAAAAAAgAAAAWgAAAHJlbW92ZS50eHRQSwECFAMUAAAAAAAFOaZYV+5xkgUAAAAFAAAACAAAAAAAAAAAAAAApIGIAAAAc2FtZS50eHRQSwECFAAUAAAAAAAFOaZYaREftgYAAAAGAAAACAAAAAAAAAAAACAAAACzAAAAc2FtZS50eHRQSwUGAAAAAAUABQASAQAA3wAAABMAY29tbWVudCBQSwECIGluc2lkZQ==";

        // The same Python fixture with the second same.txt named next.txt, so successful
        // additions can retain every platform and permission while remaining extractable.
        private const string DistinctNameFixture =
            "UEsDBBQAAAAAAAU5plgAAAAAAAAAAAAAAAAGAAAAdG9vbHMvUEsDBBQAAAAAAAU5plgIAQ5JDAAAAAwAAAAMAAAAdG9vbHMvcnVuLnNoZWNobyBleGFtcGxlUEsDBBQAAAAAAAU5plgwHYBoBgAAAAYAAAAKAAAAcmVtb3ZlLnR4dHJlbW92ZVBLAwQUAAAAAAAFOaZYV+5xkgUAAAAFAAAACAAAAHNhbWUudHh0Zmlyc3RQSwMEFAAAAAAABTmmWGkRH7YGAAAABgAAAAgAAABuZXh0LnR4dHNlY29uZFBLAQIUAxQAAAAAAAU5plgAAAAAAAAAAAAAAAAGAAAAAAAAAAAAEADtQQAAAAB0b29scy9QSwECFAMUAAAAAAAFOaZYCAEOSQwAAAAMAAAADAAAAAAAAAAAAAAA7YEkAAAAdG9vbHMvcnVuLnNoUEsBAhQAFAAAAAAABTmmWDAdgGgGAAAABgAAAAoAAAAAAAAAAAAgAAAAWgAAAHJlbW92ZS50eHRQSwECFAMUAAAAAAAFOaZYV+5xkgUAAAAFAAAACAAAAAAAAAAAAAAApIGIAAAAc2FtZS50eHRQSwECFAAUAAAAAAAFOaZYaREftgYAAAAGAAAACAAAAAAAAAAAACAAAACzAAAAbmV4dC50eHRQSwUGAAAAAAUABQASAQAA3wAAABMAY29tbWVudCBQSwECIGluc2lkZQ==";

        [Theory]
        [InlineData("add", false)]
        [InlineData("delete", false)]
        [InlineData("rename", false)]
        [InlineData("createFolder", false)]
        [InlineData("add", true)]
        [InlineData("delete", true)]
        [InlineData("rename", true)]
        [InlineData("createFolder", true)]
        public void Rewrite_PreservesForeignPlatformsAndPermissions(string operation, bool zip64)
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string path = Path.Combine(work, "foreign.zip");
            try
            {
                byte[] fixture = Convert.FromBase64String(operation == "add" ? DistinctNameFixture : Fixture);
                File.WriteAllBytes(path, zip64 ? WithZip64Directory(fixture) : fixture);
                string added = Path.Combine(work, "added.txt");
                File.WriteAllText(added, "added");
                ArchiveWriteResult result = operation switch
                {
                    "add" => ArchiveWriter.Add(path, "", new[] { added }, ArchiveCollision.Report, null, CancellationToken.None),
                    "delete" => ArchiveWriter.Delete(path, new[] { "remove.txt" }, null, CancellationToken.None),
                    "rename" => ArchiveWriter.Rename(path, "tools", "scripts", null, CancellationToken.None),
                    _ => ArchiveWriter.CreateFolder(path, "", "new", null, CancellationToken.None),
                };

                Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
                string folder = operation == "rename" ? "scripts/" : "tools/";
                var entries = ReadDirectory(path);
                Assert.Equal((byte)3, entries.Single(e => e.Name == folder).Platform);
                var executable = entries.Single(e => e.Name == folder + "run.sh");
                Assert.Equal((byte)3, executable.Platform);
                Assert.Equal(0x81ed0000u, executable.Attributes);
                string secondName = operation == "add" ? "next.txt" : "same.txt";
                var preserved = entries.Where(e => e.Name == "same.txt" || e.Name == secondName).ToArray();
                Assert.Equal(new[] { "same.txt", secondName }, preserved.Select(e => e.Name));
                Assert.Equal(new byte[] { 3, 0 }, preserved.Select(e => e.Platform));
                Assert.Equal(new uint[] { 0x81a40000, 0x20 }, preserved.Select(e => e.Attributes));
                using var archive = ZipFile.OpenRead(path);
                using var reader = new StreamReader(archive.GetEntry(folder + "run.sh")!.Open());
                Assert.Equal("echo example", reader.ReadToEnd());
                Assert.Equal(new[] { "first", "second" }, archive.Entries
                    .Where(e => e.FullName == "same.txt" || e.FullName == secondName)
                    .Select(e => { using var text = new StreamReader(e.Open()); return text.ReadToEnd(); }));
                if (operation == "add")
                {
                    string output = Path.Combine(work, "extracted");
                    Assert.True(ArchiveProvider.ExtractAll(path, output, CancellationToken.None, out string? error), error);
                    Assert.Equal("added", File.ReadAllText(Path.Combine(output, "added.txt")));
                    Assert.Equal("first", File.ReadAllText(Path.Combine(output, "same.txt")));
                    Assert.Equal("second", File.ReadAllText(Path.Combine(output, "next.txt")));
                }
            }
            finally { Directory.Delete(work, true); }
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 0)]
        [InlineData(true, 1)]
        [InlineData(true, 2)]
        public void Add_RefusesExistingDuplicateOutputsWithoutChangingBytes(bool zip64, int policy)
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string path = Path.Combine(work, "foreign.zip");
            try
            {
                byte[] fixture = Convert.FromBase64String(Fixture);
                byte[] original = zip64 ? WithZip64Directory(fixture) : fixture;
                File.WriteAllBytes(path, original);
                string added = Path.Combine(work, "added.txt");
                File.WriteAllText(added, "added");

                var result = ArchiveWriter.Add(path, "", new[] { added }, (ArchiveCollision)policy,
                    null, CancellationToken.None);

                Assert.False(result.Ok);
                Assert.Equal("Str_Status_ArchiveCollide", result.ErrorKey);
                Assert.Equal("same.txt", Assert.Single(result.Collisions));
                Assert.True(result.ExistingCollisions, "The duplicate is the archive's own, not the addition's.");
                Assert.Equal(original, File.ReadAllBytes(path));
                Assert.Equal("added", File.ReadAllText(added));
                Assert.Equal(2, Directory.GetFiles(work).Length);
            }
            finally { Directory.Delete(work, true); }
        }

        [Fact]
        public void Rewrite_PreservesPermissionsWhenOutputNeedsZip64()
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string path = Path.Combine(work, "many.zip");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    archive.CreateEntry("run.sh").ExternalAttributes = unchecked((int)0x81ed0000);
                    for (int i = 1; i < 65536; i++) archive.CreateEntry("file" + i);
                }
                byte[] before = File.ReadAllBytes(path);
                int zip64 = checked((int)BitConverter.ToUInt64(before, before.Length - 34));
                int directory = checked((int)BitConverter.ToUInt64(before, zip64 + 48));
                before[directory + 5] = 3;
                File.WriteAllBytes(path, before);

                var result = ArchiveWriter.CreateFolder(path, "", "new", null, CancellationToken.None);

                Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
                var entries = ReadDirectory(path);
                Assert.Equal(65537, entries.Count);
                Assert.Equal((byte)3, entries[0].Platform);
                Assert.Equal(0x81ed0000u, entries[0].Attributes);
                Assert.Equal("new/", entries[entries.Count - 1].Name);
            }
            finally { Directory.Delete(work, true); }
        }

        [Theory]
        [InlineData("directory-size")]
        [InlineData("zip64-offset")]
        [InlineData("zip64-count")]
        [InlineData("entry-length")]
        public void Rewrite_RefusesInvalidMetadataWithoutReplacingOriginal(string corruption)
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string path = Path.Combine(work, "invalid.zip");
            try
            {
                byte[] before = Convert.FromBase64String(Fixture);
                int end = before.Length - 41;
                if (corruption.StartsWith("zip64", StringComparison.Ordinal))
                {
                    before = WithZip64Directory(before);
                    if (corruption == "zip64-offset")
                        Array.Copy(BitConverter.GetBytes(ulong.MaxValue), 0, before, end + 64, 8);
                    else
                        Array.Copy(BitConverter.GetBytes(6L), 0, before, end + 24, 8);
                }
                else if (corruption == "directory-size")
                    Array.Copy(BitConverter.GetBytes(1u), 0, before, end + 12, 4);
                else
                {
                    int directory = checked((int)BitConverter.ToUInt32(before, end + 16));
                    before[directory + 30] = before[directory + 31] = 255;
                }
                File.WriteAllBytes(path, before);

                var result = ArchiveWriter.CreateFolder(path, "", "new", null, CancellationToken.None);

                Assert.False(result.Ok);
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.Equal(new[] { path }, Directory.GetFiles(work));
            }
            finally { Directory.Delete(work, true); }
        }

        private static byte[] WithZip64Directory(byte[] fixture)
        {
            int end = fixture.Length - 22 - 19;
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, true);
            writer.Write(fixture, 0, end);
            writer.Write(0x06064b50u);
            writer.Write(44L);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(5L);
            writer.Write(5L);
            writer.Write((long)BitConverter.ToUInt32(fixture, end + 12));
            writer.Write((long)BitConverter.ToUInt32(fixture, end + 16));
            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write((long)end);
            writer.Write(1u);
            byte[] classic = fixture.Skip(end).ToArray();
            for (int i = 8; i < 20; i++) classic[i] = 255;
            writer.Write(classic);
            return output.ToArray();
        }

        private static List<(string Name, byte Platform, uint Attributes)> ReadDirectory(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            // Rebuilt test archives have no archive comment.
            int end = data.Length - 22;
            Assert.Equal(0x06054b50u, BitConverter.ToUInt32(data, end));
            int offset = checked((int)BitConverter.ToUInt32(data, end + 16));
            int count = BitConverter.ToUInt16(data, end + 10);
            if (count == ushort.MaxValue)
            {
                Assert.Equal(0x07064b50u, BitConverter.ToUInt32(data, end - 20));
                int zip64 = checked((int)BitConverter.ToUInt64(data, end - 12));
                Assert.Equal(0x06064b50u, BitConverter.ToUInt32(data, zip64));
                count = checked((int)BitConverter.ToUInt64(data, zip64 + 32));
                offset = checked((int)BitConverter.ToUInt64(data, zip64 + 48));
            }
            var entries = new List<(string, byte, uint)>();
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(0x02014b50u, BitConverter.ToUInt32(data, offset));
                int nameLength = BitConverter.ToUInt16(data, offset + 28);
                entries.Add((Encoding.UTF8.GetString(data, offset + 46, nameLength), data[offset + 5],
                    BitConverter.ToUInt32(data, offset + 38)));
                offset += 46 + nameLength + BitConverter.ToUInt16(data, offset + 30) + BitConverter.ToUInt16(data, offset + 32);
            }
            return entries;
        }
    }
}
