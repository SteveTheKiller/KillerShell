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
        [InlineData("", "*", true, true)]
        [InlineData("", "?", true, false)]
        [InlineData("abc", "abc*", true, true)]
        [InlineData("abc", "ab", true, false)]
        [InlineData("ab", "abc", true, false)]
        [InlineData("abc", "a*?c", true, true)]
        [InlineData("ac", "a*?c", true, false)]
        [InlineData("axbxc", "a*b*c", true, true)]
        [InlineData("axbx", "a*b*c", true, false)]
        [InlineData("a+b(1).txt", "a+b(?).txt", true, true)]
        [InlineData("[x].log", "[x].*", true, true)]
        [InlineData("x.log", "[x].*", true, false)]
        [InlineData("ÄBC.txt", "äbc.*", false, true)]
        [InlineData("ÄBC.txt", "äbc.*", true, false)]
        [InlineData("日本.txt", "?本.*", true, true)]
        public void MatchesWildcard_UsesExpectedCaseAndWildcardRules(
            string input, string pattern, bool caseSensitive, bool expected)
            => Assert.Equal(expected, SearchEngine.MatchesWildcard(input, pattern, caseSensitive));

        [Theory]
        [InlineData("name-term")]
        [InlineData("include")]
        [InlineData("exclude")]
        [InlineData("one-off")]
        public async Task PathologicalWildcards_StopPromptlyWhenCanceled(string scenario)
        {
            // Run in its own process: the old regex matcher never finished these patterns, and a
            // runaway match would otherwise keep spinning inside the test host.
            string assembly = typeof(SearchEngineTests).Assembly.Location.Replace("'", "''");
            string application = typeof(SearchEngine).Assembly.Location.Replace("'", "''");
            string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
            string script = "$ErrorActionPreference = 'Stop'; try { " +
                "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
                "[KillerShell.Tests.Services.WildcardCancelScenario]::Run('" + scenario + "'); exit 0 " +
                "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                process.Kill();
                Assert.Fail("The wildcard scenario was still running after 20 seconds.");
            }
            Assert.True(process.ExitCode == 0, $"Scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
        }

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

    public static class WildcardCancelScenario
    {
        // Every * used to become .* in an unbounded regex. Twenty of them against a long name
        // that cannot match backtracks for longer than anyone will wait, and cancellation is only
        // checked between files, never inside a match.
        private static readonly string Pathological = string.Concat(Enumerable.Repeat("*a", 20)) + "*b";

        // Called by the isolated process through its public test entry point.
        public static void Run(string scenario)
        {
            if (scenario == "one-off")
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Assert.False(SearchEngine.MatchesWildcard(new string('a', 34) + ".txt", Pathological, false));
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "One wildcard test took " + watch.Elapsed);
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var files = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    string file = Path.Combine(root, new string('a', 30 + i) + ".txt");
                    File.WriteAllText(file, "x");
                    files.Add(file);
                }

                // Twice, so a match left running by the first cancel would show up in the second.
                for (int round = 0; round < 2; round++)
                {
                    var groups = new List<TermGroup>();
                    string include = string.Empty, exclude = string.Empty;
                    if (scenario == "name-term")
                    {
                        var group = new TermGroup { Mode = TermGroup.GroupMode.And };
                        group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.FileName, Pattern = Pathological });
                        groups.Add(group);
                    }
                    else if (scenario == "include") include = Pathological;
                    else if (scenario == "exclude") exclude = Pathological;

                    using var cts = new CancellationTokenSource();
                    Task search = new SearchEngine().SearchAsync(root, groups, new List<SearchFilter>(), include, exclude, false, cts.Token, files);
                    search.Wait(300);
                    cts.Cancel();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    Assert.True(search.Wait(TimeSpan.FromSeconds(1)),
                        $"Search was still running {watch.Elapsed.TotalSeconds:0.0}s after cancellation (round {round + 1}).");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
