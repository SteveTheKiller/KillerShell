using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class SearchEngineTests
    {
        [Theory]
        [InlineData("error.log", "*.log", false, true)]
        [InlineData("ERROR.LOG", "*.log", false, true)]
        [InlineData("ERROR.LOG", "*.log", true, false)]
        [InlineData("notes.txt", "n?tes.*", true, true)]
        [InlineData("notes.txt", "*.log", false, false)]
        public void MatchesWildcard_UsesExpectedCaseAndWildcardRules(
            string input, string pattern, bool caseSensitive, bool expected)
            => Assert.Equal(expected, SearchEngine.MatchesWildcard(input, pattern, caseSensitive));

        [Fact]
        public async Task SearchAsync_AppliesNameContentAndExtensionFilters()
        {
            string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string matching = Path.Combine(root, "notes.txt");
                string wrongContent = Path.Combine(root, "other.txt");
                string wrongExtension = Path.Combine(root, "notes.log");
                File.WriteAllText(matching, "first line\nneedle here");
                File.WriteAllText(wrongContent, "nothing here");
                File.WriteAllText(wrongExtension, "needle here");

                var group = new TermGroup { Mode = TermGroup.GroupMode.And };
                group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.FileName, Pattern = "notes" });
                group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = "needle" });
                var filter = new SearchFilter { FieldIndex = SearchFilter.FieldExt, Text = "txt" };
                var found = new List<SearchResult>();
                var engine = new SearchEngine();
                engine.ResultsBatch += batch => found.AddRange(batch);

                await engine.SearchAsync(root, [group], [filter], string.Empty, string.Empty,
                    false, CancellationToken.None, [matching, wrongContent, wrongExtension]);

                SearchResult result = Assert.Single(found);
                Assert.Equal(matching, result.FilePath);
                Assert.Contains(result.Matches.SelectMany(match => match.Lines), line => line.LineNumber == 2);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SearchAsync_FindsContentInUtf16BomFiles(bool bigEndian)
        {
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "needle", new UnicodeEncoding(bigEndian, byteOrderMark: true));
                var group = new TermGroup { Mode = TermGroup.GroupMode.And };
                group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = "needle" });
                var found = new List<SearchResult>();
                var engine = new SearchEngine();
                engine.ResultsBatch += batch => found.AddRange(batch);

                await engine.SearchAsync(Path.GetTempPath(), [group], [], string.Empty, string.Empty,
                    false, CancellationToken.None, [file]);

                SearchResult result = Assert.Single(found);
                Assert.Equal(file, result.FilePath);
                LineMatch line = Assert.Single(Assert.Single(result.Matches).Lines);
                Assert.Equal(1, line.LineNumber);
                Assert.Equal("needle", line.LineText);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Theory]
        [InlineData(65001, 0)]
        [InlineData(65001, 4092)]
        [InlineData(1200, 2046)]
        [InlineData(1201, 2046)]
        [InlineData(12000, 0)]
        [InlineData(12001, 0)]
        [InlineData(12000, 1023)]
        [InlineData(12001, 1023)]
        [InlineData(1200, 40000)]
        public async Task SearchAsync_DecodesBomTextAcrossReadBoundaries(int codePage, int paddingLength)
        {
            string file = Path.GetTempFileName();
            try
            {
                string prefix = paddingLength == 0 ? string.Empty : new string('x', paddingLength) + "\U0001F600\r\n";
                File.WriteAllText(file, prefix + "needle caf\u00E9", Encoding.GetEncoding(codePage));

                SearchResult result = Assert.Single(await SearchContentAsync(file));
                LineMatch line = Assert.Single(Assert.Single(result.Matches).Lines);
                Assert.Equal(paddingLength == 0 ? 1 : 2, line.LineNumber);
                Assert.Equal("needle caf\u00E9", line.LineText);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65001)]
        [InlineData(1200)]
        [InlineData(1201)]
        [InlineData(12000)]
        [InlineData(12001)]
        public async Task SearchAsync_SkipsFilesWithNullCharacters(int codePage)
        {
            string file = Path.GetTempFileName();
            try
            {
                Encoding encoding = codePage == 0 ? new UTF8Encoding(false) : Encoding.GetEncoding(codePage);
                File.WriteAllText(file, "needle\0tail", encoding);

                Assert.Empty(await SearchContentAsync(file));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task SearchAsync_EmptyAndPreambleOnlyFilesHaveNoContentMatches()
        {
            string file = Path.GetTempFileName();
            try
            {
                byte[][] samples = [[], [0xFF], [0xFF, 0xFE], [0xFE, 0xFF], [0xEF, 0xBB, 0xBF]];
                foreach (byte[] sample in samples)
                {
                    File.WriteAllBytes(file, sample);
                    Assert.Empty(await SearchContentAsync(file));
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        private static async Task<List<SearchResult>> SearchContentAsync(string file)
        {
            var group = new TermGroup { Mode = TermGroup.GroupMode.And };
            group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = "needle" });
            var found = new List<SearchResult>();
            var engine = new SearchEngine();
            engine.ResultsBatch += batch => found.AddRange(batch);

            await engine.SearchAsync(Path.GetTempPath(), [group], [], string.Empty, string.Empty,
                false, CancellationToken.None, [file]);

            return found;
        }

        [Fact]
        public async Task SearchAsync_PreCanceledTokenProducesNoResults()
        {
            string file = Path.GetTempFileName();
            try
            {
                var engine = new SearchEngine();
                var found = new List<SearchResult>();
                engine.ResultsBatch += batch => found.AddRange(batch);
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                await engine.SearchAsync(Path.GetTempPath(), [], [], string.Empty, string.Empty,
                    false, cts.Token, [file]);

                Assert.Empty(found);
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}
