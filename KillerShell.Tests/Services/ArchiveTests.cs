using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ArchiveTests
    {
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
    }
}
