using System.Linq;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class DemoArchivesTests
    {
        private const string Photos = @"C:\Users\Demo\Documents\asset-photos.zip";

        [Fact]
        public void ListedDemoArchiveOpensFoldersAndMatchingText()
        {
            Assert.True(DemoArchives.Contains(Photos));
            var root = DemoArchives.List(Photos, string.Empty);
            var readme = Assert.Single(root, entry => entry.Name == "README.md");
            Assert.False(readme.IsDirectory);
            Assert.Contains("asset-photos.zip", DemoArchives.TextFor(ArchiveProvider.Combine(Photos, readme.EntryPath)));

            var folder = Assert.Single(root, entry => entry.IsDirectory);
            var children = DemoArchives.List(Photos, folder.EntryPath);
            Assert.NotEmpty(children);
            Assert.Equal(children.Sum(entry => entry.Size), folder.Size);
            foreach (var child in children)
            {
                string path = ArchiveProvider.Combine(Photos, child.EntryPath);
                string text = DemoArchives.TextFor(path)!;
                Assert.False(string.IsNullOrEmpty(text));
                Assert.Equal(Encoding.UTF8.GetByteCount(text), child.Size);
                Assert.Equal(ArchiveProvider.Combine(Photos, folder.EntryPath), ArchiveProvider.Parent(path));
            }
        }

        [Fact]
        public void UnknownArchivesAndEntriesHaveNoFabricatedContents()
        {
            const string unknown = @"C:\Users\Other\private.zip";
            Assert.False(DemoArchives.Contains(unknown));
            Assert.Empty(DemoArchives.List(unknown, string.Empty));
            Assert.Null(DemoArchives.TextFor(ArchiveProvider.Combine(unknown, "README.md")));
            Assert.Empty(DemoArchives.List(Photos, "missing"));
            Assert.Null(DemoArchives.TextFor(ArchiveProvider.Combine(Photos, "missing.txt")));
        }

        [Fact]
        public void ArchiveTextEditsSurviveReopeningInMemory()
        {
            string path = ArchiveProvider.Combine(Photos, "README.md");
            string original = DemoArchives.TextFor(path)!;
            Assert.NotNull(original);
            DemoTextStore.Open(path, original);
            try
            {
                Assert.True(DemoTextStore.TrySave(path, "# Updated archive notes"));
                Assert.Equal("# Updated archive notes", DemoTextStore.Open(path, original));
                Assert.Equal(original, DemoArchives.TextFor(path));
            }
            finally { DemoTextStore.TrySave(path, original); }
        }
    }
}
