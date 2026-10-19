using System;
using System.IO;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class VerifiedFileTests
    {
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

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
