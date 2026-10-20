using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class FileOpsTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void PartialReplacementFailureRetainsBothContents(bool move, bool obstructRecovery)
        {
            string root = NewTempDirectory();
            try
            {
                string source = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "src")).FullName, "same.txt");
                string target = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
                string destination = Path.Combine(target, "same.txt");
                File.WriteAllText(source, "incoming");
                File.WriteAllText(destination, "previous destination");
                string? retainedBackup = null;
                var result = FileOps.CopyOrMove(new[] { source }, target, move, _ => ConflictChoice.Replace,
                    _ => { }, CancellationToken.None, (incoming, old, backup) =>
                    {
                        // Model the documented partial native failure after the old name moves.
                        if (backup == null) File.Delete(old);
                        else { File.Move(old, backup); retainedBackup = backup; }
                        if (obstructRecovery) Directory.CreateDirectory(old);
                        throw new IOException("Controlled replacement failure after old-name removal (1176).");
                    });
                Assert.Single(result.Failed);
                Assert.Equal(0, result.Succeeded);
                Assert.Equal("incoming", File.ReadAllText(source));
                Assert.NotNull(retainedBackup);
                if (!obstructRecovery) Assert.Equal("previous destination", File.ReadAllText(destination));
                else
                {
                    Assert.Equal("previous destination", File.ReadAllText(retainedBackup!));
                    Assert.Contains(retainedBackup!, result.Failed[0].Error);
                }
                if (!move)
                {
                    string staged = Assert.Single(Directory.GetFiles(target, ".KillerShell-copy-*.tmp"));
                    Assert.Equal("incoming", File.ReadAllText(staged));
                }
            }
            finally { Directory.Delete(root, true); }
        }
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void UnreadableSubtreeIsReportedUnlessWholeDirectoryWasMoved(bool move, bool fast)
        {
            string root = NewTempDirectory();
            string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            string denied = Directory.CreateDirectory(Path.Combine(source, "denied")).FullName;
            string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            string output = Path.Combine(target, "source");
            File.WriteAllText(Path.Combine(denied, "hidden.txt"), "preserve");
            File.WriteAllText(Path.Combine(source, "visible.txt"), "copy me");
            if (move && !fast) Directory.CreateDirectory(output);
            var acl = Directory.GetAccessControl(denied);
            var denyRule = new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny);
            acl.AddAccessRule(denyRule);
            Directory.SetAccessControl(denied, acl);
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFiles(denied));
                var result = FileOps.CopyOrMove(new[] { source }, target, move,
                    _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);
                Assert.False(result.Canceled);
                Assert.Equal("copy me", File.ReadAllText(Path.Combine(output, "visible.txt")));
                if (fast)
                {
                    Assert.Empty(result.Failed);
                    Assert.False(Directory.Exists(source));
                }
                else
                {
                    var failure = Assert.Single(result.Failed);
                    Assert.Equal(denied, failure.Path);
                    Assert.False(string.IsNullOrWhiteSpace(failure.Error));
                    Assert.Equal("preserve", File.ReadAllText(Path.Combine(denied, "hidden.txt")));
                    Assert.Equal(1, result.Succeeded);
                }
            }
            finally
            {
                string remaining = Directory.Exists(denied) ? denied : Path.Combine(output, "denied");
                var cleanupAcl = Directory.GetAccessControl(remaining);
                cleanupAcl.RemoveAccessRuleSpecific(denyRule);
                Directory.SetAccessControl(remaining, cleanupAcl);
                Directory.Delete(root, true);
            }
        }
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        public void CopyOrCopyThenDelete_PreservesAlternateStreams(bool replace, bool empty, bool move)
        {
            string root = NewTempDirectory();
            try
            {
                string source = Path.Combine(root, "download.txt");
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                string destination = Path.Combine(target, "download.txt");
                File.WriteAllText(source, empty ? "" : "download contents");
                WriteStream(source, "Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
                WriteStream(source, "review-metadata", "preserve this metadata");
                Assert.Equal("preserve this metadata", ReadStream(source, "review-metadata"));
                if (replace) File.WriteAllText(destination, "old content");

                // An extended source spelling takes the same copy/delete route as a different drive.
                string input = move ? @"\\?\" + source : source;
                FileOpResult result = FileOps.CopyOrMove([input], target, move,
                    _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);

                Assert.Empty(result.Failed);
                Assert.Equal(1, result.Succeeded);
                Assert.Equal(empty ? "" : "download contents", File.ReadAllText(destination));
                Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", ReadStream(destination, "Zone.Identifier"));
                Assert.Equal("preserve this metadata", ReadStream(destination, "review-metadata"));
                Assert.Equal(!move, File.Exists(source));
                Assert.Single(Directory.GetFiles(target));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void CopyReadOnlySource_PreservesStreamsAndCancellation(bool replace, bool cancel)
        {
            string root = NewTempDirectory();
            try
            {
                string source = Path.Combine(root, "source.bin");
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                string destination = Path.Combine(target, "source.bin");
                File.WriteAllBytes(source, new byte[8 * 1024 * 1024]);
                WriteStream(source, "Zone.Identifier", "ZoneId=3");
                File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
                if (replace) File.WriteAllText(destination, "old content");
                using var cancellation = new CancellationTokenSource();

                FileOpResult result = FileOps.CopyOrMove([source], target, false,
                    _ => ConflictChoice.Replace,
                    progress => { if (cancel && progress.BytesDone >= 4 * 1024 * 1024) cancellation.Cancel(); },
                    cancellation.Token);

                Assert.Empty(result.Failed);
                Assert.Equal(cancel, result.Canceled);
                Assert.Single(Directory.GetFiles(target));
                Assert.True((File.GetAttributes(source) & FileAttributes.ReadOnly) != 0);
                if (cancel) Assert.Equal("old content", File.ReadAllText(destination));
                else
                {
                    Assert.Equal(1, result.Succeeded);
                    Assert.Equal(new FileInfo(source).Length, new FileInfo(destination).Length);
                    Assert.False((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0);
                    Assert.Equal("ZoneId=3", ReadStream(destination, "Zone.Identifier"));
                }
            }
            finally
            {
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }

        private static void WriteStream(string path, string name, string contents)
        {
            using var handle = OpenStream(path + ":" + name, 0x40000000, 3, IntPtr.Zero, 2, 0, IntPtr.Zero);
            Assert.False(handle.IsInvalid, "Cannot create alternate stream: " + Marshal.GetLastWin32Error());
            using var stream = new FileStream(handle, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(contents);
        }

        private static string ReadStream(string path, string name)
        {
            using var handle = OpenStream(path + ":" + name, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            Assert.False(handle.IsInvalid, "Cannot read alternate stream: " + Marshal.GetLastWin32Error());
            using var stream = new FileStream(handle, FileAccess.Read);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenStream(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [Fact]
        public void CopyMoveRenameAndDelete_StayInsideTemporaryFolders()
        {
            string root = NewTempDirectory();
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                string copies = Directory.CreateDirectory(Path.Combine(root, "copies")).FullName;
                string moves = Directory.CreateDirectory(Path.Combine(root, "moves")).FullName;
                string original = Path.Combine(source, "sample.txt");
                File.WriteAllText(original, "sample content");

                FileOpResult copied = FileOps.CopyOrMove([original], copies, false,
                    _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);
                string copiedPath = Path.Combine(copies, "sample.txt");
                Assert.Equal(1, copied.Succeeded);
                Assert.Equal("sample content", File.ReadAllText(copiedPath));

                FileOpResult moved = FileOps.CopyOrMove([copiedPath], moves, true,
                    _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);
                string movedPath = Path.Combine(moves, "sample.txt");
                Assert.Equal(1, moved.Succeeded);
                Assert.False(File.Exists(copiedPath));
                Assert.True(File.Exists(movedPath));

                Assert.Null(FileOps.Rename(movedPath, "renamed.txt"));
                string renamedPath = Path.Combine(moves, "renamed.txt");
                Assert.True(File.Exists(renamedPath));

                FileOpResult deleted = FileOps.Delete([renamedPath], _ => { }, CancellationToken.None);
                Assert.Equal(1, deleted.Succeeded);
                Assert.False(File.Exists(renamedPath));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SuccessfulReplacement_PutsNewContentAtDestination(bool move)
        {
            string root = NewTempDirectory();
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                string original = Path.Combine(source, "sample.txt");
                string destination = Path.Combine(target, "sample.txt");
                File.WriteAllText(original, "new content");
                File.WriteAllText(destination, "old content");

                FileOpResult result = FileOps.CopyOrMove([original], target, move,
                    _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);

                Assert.Empty(result.Failed);
                Assert.Equal(1, result.Succeeded);
                Assert.Equal("new content", File.ReadAllText(destination));
                Assert.Equal(!move, File.Exists(original));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void CanceledReplacementCopy_PreservesExistingDestination()
        {
            string root = NewTempDirectory();
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                string original = Path.Combine(source, "sample.bin");
                string destination = Path.Combine(target, "sample.bin");
                File.WriteAllBytes(original, new byte[8 * 1024 * 1024]);
                File.WriteAllText(destination, "keep this file");
                using var cancellation = new CancellationTokenSource();

                FileOpResult result = FileOps.CopyOrMove([original], target, false,
                    _ => ConflictChoice.Replace,
                    progress => { if (progress.BytesDone >= 4 * 1024 * 1024) cancellation.Cancel(); },
                    cancellation.Token);

                Assert.True(result.Canceled);
                Assert.Equal("keep this file", File.ReadAllText(destination));
                Assert.Equal(0, result.Succeeded);
                Assert.Single(Directory.GetFiles(target));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void FailedReplacementMove_PreservesExistingDestination()
        {
            string root = NewTempDirectory();
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                string original = Path.Combine(source, "sample.txt");
                string destination = Path.Combine(target, "sample.txt");
                File.WriteAllText(original, "new content");
                File.WriteAllText(destination, "old content");

                using (new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    FileOpResult result = FileOps.CopyOrMove([original], target, true,
                        _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);
                    Assert.Single(result.Failed);
                    Assert.Equal(0, result.Succeeded);
                    Assert.Equal("old content", File.ReadAllText(destination));
                }
                Assert.Equal("new content", File.ReadAllText(original));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void DirectoryBatchWithLaterMerge_MovesEverySourceOnce()
        {
            string root = NewTempDirectory();
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                string first = Directory.CreateDirectory(Path.Combine(source, "first")).FullName;
                string second = Directory.CreateDirectory(Path.Combine(source, "second")).FullName;
                string existing = Directory.CreateDirectory(Path.Combine(target, "second")).FullName;
                File.WriteAllText(Path.Combine(first, "first.txt"), "first content");
                File.WriteAllText(Path.Combine(second, "second.txt"), "second content");
                File.WriteAllText(Path.Combine(existing, "existing.txt"), "existing content");

                FileOpResult result = FileOps.CopyOrMove([first, second], target, true,
                    _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);

                Assert.Empty(result.Failed);
                Assert.Equal(2, result.Succeeded);
                Assert.Equal("first content", File.ReadAllText(Path.Combine(target, "first", "first.txt")));
                Assert.Equal("second content", File.ReadAllText(Path.Combine(existing, "second.txt")));
                Assert.Equal("existing content", File.ReadAllText(Path.Combine(existing, "existing.txt")));
                Assert.False(Directory.Exists(first));
                Assert.False(Directory.Exists(second));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void DirectoryBatch_PreservesSkipBeforeLaterMerge()
        {
            string root = NewTempDirectory();
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                string skipped = Directory.CreateDirectory(Path.Combine(source, "skipped")).FullName;
                string merged = Directory.CreateDirectory(Path.Combine(source, "merged")).FullName;
                string existingSkip = Directory.CreateDirectory(Path.Combine(target, "skipped")).FullName;
                string existingMerge = Directory.CreateDirectory(Path.Combine(target, "merged")).FullName;
                File.WriteAllText(Path.Combine(skipped, "new.txt"), "do not move");
                File.WriteAllText(Path.Combine(merged, "new.txt"), "move this");
                File.WriteAllText(Path.Combine(existingSkip, "old.txt"), "keep this");

                FileOpResult result = FileOps.CopyOrMove([skipped, merged], target, true,
                    conflict => conflict.TargetPath == existingSkip ? ConflictChoice.Skip : ConflictChoice.Replace,
                    _ => { }, CancellationToken.None);

                Assert.Empty(result.Failed);
                Assert.Equal(1, result.Skipped);
                Assert.Equal("do not move", File.ReadAllText(Path.Combine(skipped, "new.txt")));
                Assert.False(File.Exists(Path.Combine(existingSkip, "new.txt")));
                Assert.Equal("move this", File.ReadAllText(Path.Combine(existingMerge, "new.txt")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void DirectoryBatchWithMatchingNames_KeepsBothDestinations()
        {
            string root = NewTempDirectory();
            try
            {
                string left = Directory.CreateDirectory(Path.Combine(root, "left", "same")).FullName;
                string right = Directory.CreateDirectory(Path.Combine(root, "right", "same")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                File.WriteAllText(Path.Combine(left, "left.txt"), "left content");
                File.WriteAllText(Path.Combine(right, "right.txt"), "right content");

                FileOpResult result = FileOps.CopyOrMove([left, right], target, true,
                    _ => ConflictChoice.KeepBoth, _ => { }, CancellationToken.None);

                Assert.Empty(result.Failed);
                Assert.Equal(2, result.Succeeded);
                Assert.Equal("left content", File.ReadAllText(Path.Combine(target, "same", "left.txt")));
                Assert.Equal("right content", File.ReadAllText(Path.Combine(target, "same (2)", "right.txt")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void CopyThenDeleteFailure_ReportsPartialMoveWithBothPaths()
        {
            string root = NewTempDirectory();
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                string original = Path.Combine(source, "sample.txt");
                string destination = Path.Combine(target, "sample.txt");
                File.WriteAllText(original, "sample content");
                string extendedSource = @"\\?\" + original;

                using (new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    FileOpResult result = FileOps.CopyOrMove([extendedSource], target, true,
                        _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);
                    Assert.Equal(0, result.Succeeded);
                    Assert.Empty(result.SucceededTargets);
                    Assert.Single(result.Failed);
                    Assert.Contains(extendedSource, result.Failed[0].Error);
                    Assert.Contains(destination, result.Failed[0].Error);
                    Assert.Equal("sample content", File.ReadAllText(destination));
                }
                Assert.Equal("sample content", File.ReadAllText(original));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void NewFolder_ChoosesAUniqueName()
        {
            string root = NewTempDirectory();
            try
            {
                Assert.Equal(Path.Combine(root, "Folder"), FileOps.NewFolder(root, "Folder"));
                Assert.Equal(Path.Combine(root, "Folder (2)"), FileOps.NewFolder(root, "Folder"));
                Assert.True(Directory.Exists(Path.Combine(root, "Folder")));
                Assert.True(Directory.Exists(Path.Combine(root, "Folder (2)")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void MovingAFolderWithAJunction_LeavesTheJunctionTargetIntact()
        {
            string root = NewTempDirectory();
            try
            {
                string victim = Directory.CreateDirectory(Path.Combine(root, "victim")).FullName;
                string victimFile = Path.Combine(victim, "keep.txt");
                File.WriteAllText(victimFile, "keep");

                string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                File.WriteAllText(Path.Combine(source, "own.txt"), "own");
                string link = Path.Combine(source, "link");
                CreateJunction(link, victim);
                string loose = Path.Combine(root, "loose.txt");
                File.WriteAllText(loose, "loose");
                string dest = Directory.CreateDirectory(Path.Combine(root, "dest")).FullName;

                // A file in the batch forces the per-file path rather than the directory rename.
                FileOps.CopyOrMove([source, loose], dest, true,
                    _ => ConflictChoice.Replace, _ => { }, CancellationToken.None);

                Assert.True(File.Exists(victimFile));
                Assert.Equal("keep", File.ReadAllText(victimFile));
                Assert.True(File.Exists(Path.Combine(dest, "source", "own.txt")));
            }
            finally
            {
                RemoveJunctions(root);
                Directory.Delete(root, true);
            }
        }

        private static void CreateJunction(string link, string target)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(Directory.Exists(link), "junction was not created");
        }

        private static void RemoveJunctions(string root)
        {
            foreach (string d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(d);
            }
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
