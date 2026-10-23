using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class DemoSearchTests
    {
        private const string Invoices = @"C:\Users\Demo\Documents\Invoices";

        private static TermGroup Name(string pattern)
        {
            var group = new TermGroup();
            group.Terms.Add(new SearchTerm { Pattern = pattern });
            return group;
        }

        [Fact]
        public async Task ComputerAndOverlappingScopesReturnOnlyUniqueDemoFiles()
        {
            var found = await DemoSearch.SearchAsync([@"C:\", @"C:\Users\Demo"], [Name("invoice")], [],
                "", "", false, CancellationToken.None, _ => throw new InvalidOperationException("Name search read content"));
            Assert.NotEmpty(found.Results);
            Assert.True(found.Scanned > found.Results.Count);
            Assert.Equal(found.Results.Count, found.Results.Select(row => row.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(found.Results, row => Assert.NotNull(DemoFs.Find(row.FilePath)));
            Assert.Contains(found.Results, row => row.FilePath == Invoices + @"\invoice_2026-03.pdf");
        }

        [Fact]
        public async Task IncludeExcludeAndMetadataFiltersUseFabricatedValues()
        {
            var found = await DemoSearch.SearchAsync([Invoices], [Name("invoice")],
                [new SearchFilter { FieldIndex = SearchFilter.FieldExt, Text = ".pdf" },
                 new SearchFilter { FieldIndex = SearchFilter.FieldSize, SizeText = "100" },
                 new SearchFilter { FieldIndex = SearchFilter.FieldDate, Date = new DateTime(2026, 1, 1), ConditionIndex = 1 }],
                ".pdf", "invoice_2025*", false, CancellationToken.None, _ => "");
            Assert.NotEmpty(found.Results);
            Assert.All(found.Results, row =>
            {
                Assert.True(row.SizeBytes > 100 * 1024);
                Assert.True(row.Modified >= new DateTime(2026, 1, 2));
                Assert.StartsWith("invoice_2026", row.FileName);
                Assert.Equal(DemoFs.Find(row.FilePath)!.Created, row.Created);
            });
        }

        [Fact]
        public async Task ContentGroupsKeepLineNumbersAndCapDisplayedMatches()
        {
            var group = Name("profile");
            group.Mode = TermGroup.GroupMode.And;
            group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = "needle" });
            string content = "heading\n" + string.Join("\n", Enumerable.Repeat("needle in sample", 120));
            var found = await DemoSearch.SearchAsync([@"C:\Users\Demo\Documents\PowerShell"], [group], [],
                "", "", false, CancellationToken.None, _ => content);
            var result = Assert.Single(found.Results);
            Assert.Equal(2, result.Matches.Count);
            var lines = result.Matches.Single(match => match.Term.IsContent).Lines;
            Assert.Equal(100, lines.Count);
            Assert.Equal(2, lines[0].LineNumber);
            Assert.Equal("needle in sample", lines[0].LineText);
        }

        [Fact]
        public async Task PipedUnknownPathsAreRejectedBeforeReadingSampleContent()
        {
            var group = new TermGroup();
            group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = "prompt" });
            var found = await DemoSearch.SearchAsync([@"C:\"], [group], [], "", "", false, CancellationToken.None,
                path => DemoProfiles.TextFor(path) ?? throw new InvalidOperationException("Unknown path reached content"),
                [DemoProfiles.PowerShellPath, @"C:\Users\Other\private.txt", DemoProfiles.PowerShellPath]);
            Assert.Equal(DemoProfiles.PowerShellPath, Assert.Single(found.Results).FilePath);
            Assert.Equal(1, found.Scanned);
        }

        [Fact]
        public async Task NameCaseSensitivityAndExcludedFolderSegmentsMatchSearchRules()
        {
            var insensitive = await DemoSearch.SearchAsync([Invoices], [Name("INVOICE")], [], "*.*", "", false, CancellationToken.None, _ => "");
            var sensitive = await DemoSearch.SearchAsync([Invoices], [Name("INVOICE")], [], "", "", true, CancellationToken.None, _ => "");
            var excluded = await DemoSearch.SearchAsync([@"C:\Users\Demo"], [Name("*.pdf")], [], "", "Invoices", false, CancellationToken.None, _ => "");
            Assert.NotEmpty(insensitive.Results);
            Assert.Empty(sensitive.Results);
            Assert.DoesNotContain(excluded.Results, row => row.Directory == Invoices);
        }

        [Fact]
        public async Task GroupsAreAndedWhileEachOrGroupAcceptsEitherTerm()
        {
            var either = Name("invoice");
            either.Terms.Add(new SearchTerm { Pattern = "profile" });
            var found = await DemoSearch.SearchAsync([@"C:\"], [either, Name(".ps1")], [], "", "", false,
                CancellationToken.None, _ => "", [Invoices + @"\invoice_2026-03.pdf", DemoProfiles.PowerShellPath]);
            Assert.Equal(DemoProfiles.PowerShellPath, Assert.Single(found.Results).FilePath);
        }

        [Fact]
        public async Task MetadataOnlySearchWorksWithoutTerms()
        {
            var found = await DemoSearch.SearchAsync([@"C:\Users\Demo\Documents\PowerShell"], [],
                [new SearchFilter { Text = "ps1" }], "", "", false, CancellationToken.None,
                _ => throw new InvalidOperationException("Metadata search read content"));
            Assert.Equal(DemoProfiles.PowerShellPath, Assert.Single(found.Results).FilePath);
        }

        [Fact]
        public async Task CancellationDoesNotReadAnyContent()
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DemoSearch.SearchAsync([@"C:\"], [Name("*")], [],
                "", "", false, canceled.Token, _ => throw new InvalidOperationException("Canceled search read content")));
        }
    }
}
