using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
