using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using KillerShell.Services;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class VerifiedFileTests
    {
        [Fact]
        public void FailedImagePublicationRetainsCompleteReplacementAndOriginal()
        {
            string root = NewTempDirectory();
            try
            {
                string target = Path.Combine(root, "App.exe");
                File.WriteAllText(target, "original");
                using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var error = Assert.ThrowsAny<IOException>(() =>
                        VerifiedFile.ReplaceImage(target, Encoding.UTF8.GetBytes("replacement")));
                    Assert.Equal("original", File.ReadAllText(target));
                    string recovery = Assert.Single(Directory.GetFiles(root, "App.exe.new*"));
                    Assert.Equal("replacement", File.ReadAllText(recovery));
                    Assert.Contains(recovery, error.Message);
                }
            }
            finally { Directory.Delete(root, true); }
        }
        [Fact]
        public void MatchingHash_WritesTheVerifiedBytes()
        {
            string root = NewTempDirectory();
            try
            {
                string staged = Path.Combine(root, "staged.bin");
                string target = Path.Combine(root, "target.txt");
                File.WriteAllText(staged, "new content");
                File.WriteAllText(target, "old content");

                bool ok = VerifiedFile.TryCopy(staged, VerifiedFile.Sha256(File.ReadAllBytes(staged)), target);

                Assert.True(ok);
                Assert.Equal("new content", File.ReadAllText(target));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void SwappedStagedFile_IsRefusedAndTargetKept()
        {
            string root = NewTempDirectory();
            try
            {
                string staged = Path.Combine(root, "staged.bin");
                string target = Path.Combine(root, "target.txt");
                File.WriteAllText(staged, "what was verified");
                string hash = VerifiedFile.Sha256(File.ReadAllBytes(staged));
                File.WriteAllText(staged, "what an attacker swapped in");
                File.WriteAllText(target, "old content");

                Assert.False(VerifiedFile.TryCopy(staged, hash, target));
                Assert.Equal("old content", File.ReadAllText(target));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ReplaceImage_SwapsTheFileAndKeepsTheOldOneAside()
        {
            string root = NewTempDirectory();
            try
            {
                string target = Path.Combine(root, "App.exe");
                File.WriteAllText(target, "v1");
                byte[] v2 = Encoding.UTF8.GetBytes("v2");

                VerifiedFile.ReplaceImage(target, v2);

                Assert.Equal("v2", File.ReadAllText(target));
                Assert.False(File.Exists(target + ".new"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ReplaceImage_WorksWhileTheTargetIsOpen()
        {
            string root = NewTempDirectory();
            try
            {
                string target = Path.Combine(root, "App.exe");
                File.WriteAllText(target, "v1");

                // A running image is held open with delete sharing, which allows a rename but
                // not an overwrite. The replacement has to go around it, not through it.
                using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    VerifiedFile.ReplaceImage(target, Encoding.UTF8.GetBytes("v2"));

                Assert.Equal("v2", File.ReadAllText(target));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void FailedWrite_KeepsTheOriginalBytes()
        {
            // Another program holding a byte-range lock lets the old write open and truncate the
            // file, then refuses the write itself, which left an empty file behind.
            string root = NewTempDirectory();
            try
            {
                string target = Path.Combine(root, "target.txt");
                byte[] original = Encoding.UTF8.GetBytes("original content that must survive");
                File.WriteAllBytes(target, original);
                string staged = Stage(root, "replacement");

                using (var other = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    other.Lock(0, 4096);
                    Assert.False(VerifiedFile.TryCopy(staged, HashOf(staged), target));
                    Assert.Equal(original.Length, new FileInfo(target).Length);   // the locked bytes cannot be read until unlocked
                    other.Unlock(0, 4096);
                }
                Assert.Equal(original, File.ReadAllBytes(target));
                AssertNoLeftovers(root, "target.txt", "staged.bin");
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void NewTarget_IsCreated()
        {
            string root = NewTempDirectory();
            try
            {
                string target = Path.Combine(root, "new.txt");
                string staged = Stage(root, "fresh");
                Assert.True(VerifiedFile.TryCopy(staged, HashOf(staged), target));
                Assert.Equal("fresh", File.ReadAllText(target));
                AssertNoLeftovers(root, "new.txt", "staged.bin");
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void Replacement_KeepsAttributesPermissionsStreamsAndCreationTime()
        {
            string root = NewTempDirectory();
            try
            {
                string target = Path.Combine(root, "target.txt");
                File.WriteAllText(target, "old content");
                var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetCreationTimeUtc(target, created);
                WriteStream(target, "Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
                var acl = File.GetAccessControl(target);
                var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                acl.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.ReadData, AccessControlType.Allow));
                File.SetAccessControl(target, acl);
                File.SetAttributes(target, FileAttributes.Hidden | FileAttributes.Archive);

                string staged = Stage(root, "new content");
                Assert.True(VerifiedFile.TryCopy(staged, HashOf(staged), target));

                Assert.Equal("new content", File.ReadAllText(target));
                Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.Hidden), "Hidden attribute was lost.");
                Assert.Equal(created, File.GetCreationTimeUtc(target));
                Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", ReadStream(target, "Zone.Identifier"));
                bool kept = File.GetAccessControl(target).GetAccessRules(true, false, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .Any(r => r.IdentityReference.Equals(everyone) && r.AccessControlType == AccessControlType.Allow
                              && r.FileSystemRights.HasFlag(FileSystemRights.ReadData));
                Assert.True(kept, "The explicit permission entry was lost.");
                File.SetAttributes(target, FileAttributes.Normal);
                AssertNoLeftovers(root, "target.txt", "staged.bin");
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void HardLinkedTarget_UpdatesEveryName()
        {
            string root = NewTempDirectory();
            try
            {
                string target = Path.Combine(root, "target.txt");
                string second = Path.Combine(root, "second-name.txt");
                File.WriteAllText(target, "old content that is longer than the new one");
                Assert.True(CreateHardLink(second, target, IntPtr.Zero), "Could not create the hard link fixture.");

                string staged = Stage(root, "new");
                Assert.True(VerifiedFile.TryCopy(staged, HashOf(staged), target));

                Assert.Equal("new", File.ReadAllText(target));
                Assert.Equal("new", File.ReadAllText(second));
                AssertNoLeftovers(root, "target.txt", "second-name.txt", "staged.bin");
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void SymbolicLinkTarget_WritesThroughTheLink()
        {
            string root = NewTempDirectory();
            try
            {
                string real = Path.Combine(root, "real.txt");
                string link = Path.Combine(root, "link.txt");
                File.WriteAllText(real, "old content");
                // Unprivileged symbolic links need Windows Developer Mode.
                Assert.True(CreateSymbolicLink(link, real, 0x2), "Could not create the symbolic link fixture; turn on Developer Mode.");

                string staged = Stage(root, "new content");
                Assert.True(VerifiedFile.TryCopy(staged, HashOf(staged), link));

                Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "The link was replaced by a plain file.");
                Assert.Equal("new content", File.ReadAllText(real));
                AssertNoLeftovers(root, "real.txt", "link.txt", "staged.bin");
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void TargetOpenElsewhereForReadingAndWriting_StillSaves()
        {
            // Log viewers keep files open sharing read and write but not delete. Saving over such
            // a file worked before and must keep working.
            string root = NewTempDirectory();
            try
            {
                string target = Path.Combine(root, "target.txt");
                File.WriteAllText(target, "old content");
                string staged = Stage(root, "new content, longer than before");
                using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    Assert.True(VerifiedFile.TryCopy(staged, HashOf(staged), target));
                Assert.Equal("new content, longer than before", File.ReadAllText(target));
                AssertNoLeftovers(root, "target.txt", "staged.bin");
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void DeniedTarget_IsRefusedAndKept()
        {
            string root = NewTempDirectory();
            string target = Path.Combine(root, "target.txt");
            var me = WindowsIdentity.GetCurrent().User!;
            var deny = new FileSystemAccessRule(me, FileSystemRights.WriteData | FileSystemRights.AppendData, AccessControlType.Deny);
            try
            {
                File.WriteAllText(target, "protected");
                var acl = File.GetAccessControl(target);
                acl.AddAccessRule(deny);
                File.SetAccessControl(target, acl);

                string staged = Stage(root, "replacement");
                Assert.False(VerifiedFile.TryCopy(staged, HashOf(staged), target));
                Assert.Equal("protected", File.ReadAllText(target));
                AssertNoLeftovers(root, "target.txt", "staged.bin");
            }
            finally
            {
                try { var acl = File.GetAccessControl(target); acl.RemoveAccessRule(deny); File.SetAccessControl(target, acl); } catch { }
                Directory.Delete(root, true);
            }
        }

        private static string Stage(string root, string text)
        {
            string staged = Path.Combine(root, "staged.bin");
            File.WriteAllText(staged, text);
            return staged;
        }

        private static string HashOf(string path) => VerifiedFile.Sha256(File.ReadAllBytes(path));

        private static void AssertNoLeftovers(string root, params string[] expected)
        {
            var names = Directory.GetFiles(root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
        }

        private static void WriteStream(string path, string stream, string text)
        {
            using var handle = CreateFile(path + ":" + stream, 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero);
            Assert.False(handle.IsInvalid, "Could not create the alternate stream fixture.");
            using var fs = new FileStream(handle, FileAccess.Write);
            byte[] data = Encoding.ASCII.GetBytes(text);
            fs.Write(data, 0, data.Length);
        }

        private static string ReadStream(string path, string stream)
        {
            using var handle = CreateFile(path + ":" + stream, 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return "<missing>";
            using var fs = new FileStream(handle, FileAccess.Read);
            using var reader = new StreamReader(fs, Encoding.ASCII);
            return reader.ReadToEnd();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                                        uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string link, string existing, IntPtr security);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
