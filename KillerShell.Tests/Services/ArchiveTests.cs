using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ArchiveTests
    {
        [Theory]
        [InlineData(".zip", false)]
        [InlineData(".zip", true)]
        [InlineData(".tar", false)]
        [InlineData(".tar", true)]
        public void ExtractAll_KeepsUnsafeNamesInsideTheDestination(string extension, bool rooted)
        {
            string name = "outside-" + Guid.NewGuid().ToString("N") + ".txt";
            string outside = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", name);
            string entryName = rooted ? outside.Replace('\\', '/') : "../" + name;
            string path = CreateCaseArchive(extension, entryName);
            if (!rooted) outside = Path.Combine(Path.GetDirectoryName(path)!, name);
            string destination = Path.Combine(Path.GetDirectoryName(path)!, "out");
            File.WriteAllText(outside, "untouched");
            try
            {
                bool extracted = ArchiveProvider.ExtractAll(path, destination, CancellationToken.None, out string? error);

                Assert.Equal(!rooted, extracted);
                Assert.Equal("untouched", File.ReadAllText(outside));
                if (rooted)
                {
                    Assert.NotNull(error);
                    Assert.Empty(Directory.GetFileSystemEntries(destination));
                }
                else
                {
                    Assert.Null(error);
                    Assert.Equal(entryName, File.ReadAllText(Path.Combine(destination, name)));
                }
            }
            finally
            {
                File.Delete(outside);
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

        [Theory]
        [InlineData(".zip", false)]
        [InlineData(".zip", true)]
        [InlineData(".tar", false)]
        [InlineData(".tar", true)]
        public void ExtractAll_RejectsCaseCollisionsBeforeWritingSiblings(string extension, bool folder)
        {
            string path = folder
                ? CreateCaseArchive(extension, "Docs/upper.txt", "docs/lower.txt")
                : CreateCaseArchive(extension, "A.txt", "a.txt");
            string destination = Path.Combine(Path.GetDirectoryName(path)!, "out");
            try
            {
                bool extracted = ArchiveProvider.ExtractAll(path, destination, CancellationToken.None, out string? error);

                Assert.False(extracted);
                Assert.Contains("same output path", error);
                Assert.Empty(Directory.GetFileSystemEntries(destination));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

        [Theory]
        [InlineData("docs", true)]
        [InlineData("Docs", false)]
        public void ZipCreateFolder_CollidesOnlyWithTheExactCaseDirectory(string name, bool allowed)
        {
            string path = CreateCaseArchive(".zip", "Docs/existing.txt");
            try
            {
                byte[] original = File.ReadAllBytes(path);

                var result = ArchiveWriter.CreateFolder(path, "", name, null, CancellationToken.None);

                Assert.Equal(allowed, result.Ok);
                if (allowed)
                {
                    using var archive = ZipFile.OpenRead(path);
                    Assert.Equal(new[] { "Docs/existing.txt", "docs/" }, archive.Entries.Select(entry => entry.FullName));
                }
                else
                {
                    Assert.Equal("Str_Status_ArchiveNameTaken", result.ErrorKey);
                    Assert.Equal(original, File.ReadAllBytes(path));
                }
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ZipRename_CaseOnlyRenameProtectsExistingSibling(bool folder, bool targetExists)
        {
            string[] names = folder
                ? (targetExists ? new[] { "Docs/upper.txt", "docs/lower.txt" } : new[] { "docs/lower.txt" })
                : (targetExists ? new[] { "A.txt", "a.txt" } : new[] { "a.txt" });
            string path = CreateCaseArchive(".zip", names);
            try
            {
                byte[] original = File.ReadAllBytes(path);

                var result = ArchiveWriter.Rename(path, folder ? "docs" : "a.txt", folder ? "Docs" : "A.txt", null, CancellationToken.None);

                if (targetExists)
                {
                    Assert.False(result.Ok);
                    Assert.Equal("Str_Status_ArchiveNameTaken", result.ErrorKey);
                    Assert.Equal(original, File.ReadAllBytes(path));
                }
                else
                {
                    Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
                    Assert.Equal(1, result.Changed);
                    using var archive = ZipFile.OpenRead(path);
                    Assert.Equal(folder ? "Docs/lower.txt" : "A.txt", Assert.Single(archive.Entries).FullName);
                }
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(2, true)]
        public void ZipAdd_CollidesOnlyWithTheExactCaseName(int policy, bool exactNameExists)
        {
            string path = exactNameExists
                ? CreateCaseArchive(".zip", "A.txt", "a.txt")
                : CreateCaseArchive(".zip", "A.txt");
            try
            {
                byte[] original = File.ReadAllBytes(path);
                string source = Path.Combine(Path.GetDirectoryName(path)!, "a.txt");
                File.WriteAllText(source, "new content");

                var result = ArchiveWriter.Add(path, "", new[] { source }, (ArchiveCollision)policy, null, CancellationToken.None);

                if (exactNameExists && policy == (int)ArchiveCollision.Report)
                {
                    Assert.False(result.Ok);
                    Assert.Equal("a.txt", Assert.Single(result.Collisions));
                    Assert.Equal(original, File.ReadAllBytes(path));
                    return;
                }
                Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
                Assert.Equal(1, result.Changed);
                using var archive = ZipFile.OpenRead(path);
                string added = exactNameExists && policy == (int)ArchiveCollision.KeepBoth ? "a (2).txt" : "a.txt";
                Assert.Equal(added == "a.txt" ? new[] { "A.txt", "a.txt" } : new[] { "A.txt", "a.txt", "a (2).txt" },
                    archive.Entries.Select(entry => entry.FullName));
                using (var reader = new StreamReader(archive.GetEntry("A.txt")!.Open())) Assert.Equal("A.txt", reader.ReadToEnd());
                using (var reader = new StreamReader(archive.GetEntry(added)!.Open())) Assert.Equal("new content", reader.ReadToEnd());
                if (added != "a.txt")
                    using (var reader = new StreamReader(archive.GetEntry("a.txt")!.Open())) Assert.Equal("a.txt", reader.ReadToEnd());
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ZipEdits_ChangeOnlyTheExactCaseSelection(bool folder, bool rename)
        {
            string path = folder
                ? CreateCaseArchive(".zip", "Docs/", "Docs/A.txt", "docs/", "docs/a.txt")
                : CreateCaseArchive(".zip", "A.txt", "a.txt");
            try
            {
                string selected = folder ? "docs" : "a.txt";
                var result = rename
                    ? ArchiveWriter.Rename(path, selected, "saved", null, CancellationToken.None)
                    : ArchiveWriter.Delete(path, new[] { selected }, null, CancellationToken.None);

                Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
                Assert.Equal(folder ? 2 : 1, result.Changed);
                using var archive = ZipFile.OpenRead(path);
                string[] expected = folder
                    ? (rename ? new[] { "Docs/", "Docs/A.txt", "saved/", "saved/a.txt" } : new[] { "Docs/", "Docs/A.txt" })
                    : (rename ? new[] { "A.txt", "saved" } : new[] { "A.txt" });
                Assert.Equal(expected, archive.Entries.Select(entry => entry.FullName));
                string untouched = folder ? "Docs/A.txt" : "A.txt";
                using var reader = new StreamReader(archive.GetEntry(untouched)!.Open());
                Assert.Equal(untouched, reader.ReadToEnd());
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

        [Theory]
        [InlineData(".zip")]
        [InlineData(".tar")]
        public void List_KeepsCaseDistinctDirectoriesSeparate(string extension)
        {
            string path = CreateCaseArchive(extension, "Docs/upper.txt", "docs/lower.txt");
            string? extracted = null;
            try
            {
                var roots = ArchiveProvider.List(path, "", out string? error);
                Assert.Null(error);
                Assert.Equal(new[] { "Docs", "docs" }, roots.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal));

                var children = ArchiveProvider.List(path, "docs", out error);
                Assert.Null(error);
                Assert.Equal("docs/lower.txt", Assert.Single(children).EntryPath);
                extracted = ArchiveProvider.ExtractFolderToTemp(path, "docs", out error);
                Assert.Null(error);
                Assert.Equal("docs/lower.txt", File.ReadAllText(Path.Combine(extracted!, "lower.txt")));
                Assert.False(File.Exists(Path.Combine(extracted!, "upper.txt")));
            }
            finally
            {
                if (extracted != null) Directory.Delete(Path.GetDirectoryName(extracted)!, true);
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

        [Theory]
        [InlineData(".zip")]
        [InlineData(".tar")]
        public void ExtractToTemp_SelectsTheExactCaseEntry(string extension)
        {
            string path = CreateCaseArchive(extension, "A.txt", "a.txt");
            string? extracted = null;
            try
            {
                extracted = ArchiveProvider.ExtractToTemp(path, "a.txt", out string? error);

                Assert.Null(error);
                Assert.Equal("a.txt", File.ReadAllText(extracted!));
            }
            finally
            {
                if (extracted != null) Directory.Delete(Path.GetDirectoryName(extracted)!, true);
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }

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

        private static string CreateCaseArchive(string extension, params string[] names)
        {
            string work = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string path = Path.Combine(work, "cases" + extension);
            if (extension == ".zip")
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                foreach (string name in names)
                {
                    var entry = archive.CreateEntry(name);
                    if (!name.EndsWith("/", StringComparison.Ordinal))
                    {
                        using var writer = new StreamWriter(entry.Open());
                        writer.Write(name);
                    }
                }
            }
            else
            {
                using var archive = File.Create(path);
                foreach (string name in names) WriteTarFile(archive, name, name);
            }
            return path;
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
