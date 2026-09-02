using System;
using System.IO;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class FileOpsTests
    {
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

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
