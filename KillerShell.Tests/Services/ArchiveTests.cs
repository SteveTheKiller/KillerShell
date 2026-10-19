using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ArchiveTests
    {
        [Theory]
        [InlineData("add")]
        [InlineData("delete")]
        [InlineData("rename")]
        [InlineData("createFolder")]
        public void ZipRewrites_PreserveRetainedEntryAttributes(string operation)
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(work, "metadata.zip");
            const int fileAttributes = unchecked((int)0x81ed0000);
            const int directoryAttributes = 0x41ed0010;
            var modified = new DateTimeOffset(2024, 5, 6, 7, 8, 10, TimeSpan.Zero);
            Directory.CreateDirectory(work);
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    var directory = archive.CreateEntry("tools/");
                    directory.ExternalAttributes = directoryAttributes;
                    directory.LastWriteTime = modified;
                    var entry = archive.CreateEntry("tools/run.sh");
                    entry.ExternalAttributes = fileAttributes;
                    entry.LastWriteTime = modified;
                    using (var writer = new StreamWriter(entry.Open())) writer.Write("echo example");
                    archive.CreateEntry("remove.txt");
                }
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
                using var rewritten = ZipFile.OpenRead(path);
                string folder = operation == "rename" ? "scripts/" : "tools/";
                var retainedFile = rewritten.GetEntry(folder + "run.sh")!;
                Assert.Equal(fileAttributes, retainedFile.ExternalAttributes);
                Assert.Equal(modified.DateTime, retainedFile.LastWriteTime.DateTime);
                using (var reader = new StreamReader(retainedFile.Open())) Assert.Equal("echo example", reader.ReadToEnd());
                var retainedDirectory = rewritten.GetEntry(folder)!;
                Assert.Equal(directoryAttributes, retainedDirectory.ExternalAttributes);
                Assert.Equal(modified.DateTime, retainedDirectory.LastWriteTime.DateTime);
            }
            finally
            {
                Directory.Delete(work, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OversizedTarMetadata_IsRejected(bool extract)
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N") + ".tar");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                var header = new byte[512];
                header[156] = (byte)'x';
                byte[] size = Encoding.ASCII.GetBytes(Convert.ToString(8 * 1024 * 1024 + 1, 8).PadLeft(11, '0') + "\0");
                Array.Copy(size, 0, header, 124, size.Length);
                File.WriteAllBytes(path, header);

                string? error;
                if (extract) Assert.Null(ArchiveProvider.ExtractToTemp(path, "entry.txt", out error));
                else ArchiveProvider.List(path, "", out error);
                Assert.Contains("metadata", error, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TarMetadataAtLimit_CanBeListedAndExtracted()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N") + ".tar");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string? extracted = null;
            try
            {
                using (var archive = File.Create(path))
                {
                    WriteTarHeader(archive, "././@LongLink", 8 * 1024 * 1024, 'L');
                    var metadata = new byte[8 * 1024 * 1024];
                    Encoding.UTF8.GetBytes("entry.txt\0").CopyTo(metadata, 0);
                    archive.Write(metadata, 0, metadata.Length);
                    WriteTarHeader(archive, "short.txt", 5, '0');
                    archive.Write(Encoding.UTF8.GetBytes("hello"), 0, 5);
                    archive.Write(new byte[507], 0, 507);
                }

                var entries = ArchiveProvider.List(path, "", out string? listError);
                Assert.Null(listError);
                Assert.Equal("entry.txt", Assert.Single(entries).Name);
                extracted = ArchiveProvider.ExtractToTemp(path, "entry.txt", out string? extractError);
                Assert.Null(extractError);
                Assert.Equal("hello", File.ReadAllText(extracted!));
            }
            finally
            {
                if (extracted != null) Directory.Delete(Path.GetDirectoryName(extracted)!, true);
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("paper.zip", 1)]
        [InlineData("backup.tar.gz", 1)]
        [InlineData("backup.7z", 2)]
        [InlineData("notes.txt", 0)]
        public void Classify_RecognizesSupportedFormats(string path, int expected)
            => Assert.Equal(expected, (int)ArchiveProvider.Classify(path));

        [Fact]
        public void VirtualPaths_RoundTripAndFindParent()
        {
            string path = ArchiveProvider.Combine(@"C:\work\paper.zip", "docs/forms/page.txt");

            Assert.True(ArchiveProvider.TrySplit(path, out string archive, out string entry));
            Assert.Equal(@"C:\work\paper.zip", archive);
            Assert.Equal("docs/forms/page.txt", entry);
            Assert.Equal(@"C:\work\paper.zip?docs/forms", ArchiveProvider.Parent(path));
        }

        [Theory]
        [InlineData("docs\\forms\\page.txt", "docs/forms/page.txt")]
        [InlineData("../../safe.txt", "safe.txt")]
        [InlineData("C:/temp/report.txt", "temp/report.txt")]
        public void SafeEntryName_NormalizesSafeRelativeNames(string input, string expected)
            => Assert.Equal(expected, ArchiveWriter.SafeEntryName(input));

        [Theory]
        [InlineData("")]
        [InlineData("..")]
        [InlineData("bad?.txt")]
        public void SafeEntryName_RejectsEmptyOrInvalidNames(string input)
            => Assert.Null(ArchiveWriter.SafeEntryName(input));

        [Fact]
        public void ExtractAll_LeavesNoStagingFoldersInTemp()
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(work, "sample.tar");
            string dest = Path.Combine(work, "out");
            string staging = Path.Combine(Path.GetTempPath(), "KillerShell", "archive");
            Directory.CreateDirectory(work);
            try
            {
                using (var archive = File.Create(path))
                {
                    WriteTarFile(archive, "one.txt", "first");
                    WriteTarFile(archive, "two.txt", "second");
                }
                int before = Directory.Exists(staging) ? Directory.GetDirectories(staging).Length : 0;

                Assert.True(ArchiveProvider.ExtractAll(path, dest, System.Threading.CancellationToken.None, out string? error), error);

                Assert.Equal("first", File.ReadAllText(Path.Combine(dest, "one.txt")));
                Assert.Equal("second", File.ReadAllText(Path.Combine(dest, "two.txt")));
                int after = Directory.Exists(staging) ? Directory.GetDirectories(staging).Length : 0;
                Assert.Equal(before, after);
            }
            finally
            {
                Directory.Delete(work, true);
            }
        }

        [Fact]
        public void ExtractedEntries_KeepTheArchivesDownloadMark()
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(work, "download.tar");
            string dest = Path.Combine(work, "out");
            Directory.CreateDirectory(work);
            string? temp = null;
            try
            {
                using (var archive = File.Create(path))
                    WriteTarFile(archive, "run.txt", "payload");
                Cmd("echo [ZoneTransfer]>\"" + path + ":Zone.Identifier\" & echo ZoneId=3 >>\"" + path + ":Zone.Identifier\"");
                Assert.Contains("ZoneId=3", ReadZone(path));

                temp = ArchiveProvider.ExtractToTemp(path, "run.txt", out string? error);
                Assert.Null(error);
                Assert.Contains("ZoneId=3", ReadZone(temp!));

                Assert.True(ArchiveProvider.ExtractAll(path, dest, System.Threading.CancellationToken.None, out error), error);
                Assert.Contains("ZoneId=3", ReadZone(Path.Combine(dest, "run.txt")));
            }
            finally
            {
                if (temp != null) Directory.Delete(Path.GetDirectoryName(temp)!, true);
                Directory.Delete(work, true);
            }
        }

        [Fact]
        public void ExtractedEntries_FromAnUnmarkedArchive_StayUnmarked()
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(work, "local.tar");
            Directory.CreateDirectory(work);
            string? temp = null;
            try
            {
                using (var archive = File.Create(path))
                    WriteTarFile(archive, "run.txt", "payload");

                temp = ArchiveProvider.ExtractToTemp(path, "run.txt", out string? error);
                Assert.Null(error);
                Assert.Equal("", ReadZone(temp!));
            }
            finally
            {
                if (temp != null) Directory.Delete(Path.GetDirectoryName(temp)!, true);
                Directory.Delete(work, true);
            }
        }

        private static string Cmd(string command)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + command)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        private static string ReadZone(string file) => Cmd("more < \"" + file + ":Zone.Identifier\"").Trim();

        private static void WriteTarFile(Stream archive, string name, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            WriteTarHeader(archive, name, bytes.Length, '0');
            archive.Write(bytes, 0, bytes.Length);
            archive.Write(new byte[512 - bytes.Length % 512], 0, 512 - bytes.Length % 512);
        }

        private static void WriteTarHeader(Stream archive, string name, long size, char type)
        {
            var header = new byte[512];
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, header, nameBytes.Length);
            byte[] sizeBytes = Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0");
            Array.Copy(sizeBytes, 0, header, 124, sizeBytes.Length);
            header[156] = (byte)type;
            archive.Write(header, 0, header.Length);
        }
    }
}
