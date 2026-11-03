using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Services
{
    /// <summary>
    /// Files the app writes and settings it stores are read later, often under another region,
    /// so they never follow the Windows region: th-TH would write the year 2569 and fi-FI would
    /// write 14.30 for a time.
    /// </summary>
    public sealed class CultureInvariantExportTests
    {
        private static void UnderCulture(string name, Action body)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                body();
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        [Theory]
        [InlineData("th-TH")]
        [InlineData("fi-FI")]
        public void Csv_WritesTheModifiedDateAsInvariantIso(string culture)
        {
            UnderCulture(culture, () =>
            {
                string csv = CsvExport.Build(new[]
                {
                    new SearchResult { FileName = "a.txt", Directory = @"C:\Data", Modified = new DateTime(2026, 11, 2, 14, 30, 5) },
                });
                Assert.Contains("\"2026-11-02 14:30:05\"", csv);
            });
        }

        [Fact]
        public void Csv_LeavesAnEmptyDateEmpty()
        {
            string csv = CsvExport.Build(new[] { new SearchResult { FileName = "a.txt", Directory = @"C:\Data" } });
            Assert.Contains(",\"\",", csv);
        }

        [Theory]
        [InlineData("th-TH")]
        [InlineData("de-DE")]
        public void HtmlExport_WritesYearsAndSizesInvariant(string culture)
        {
            UnderCulture(culture, () =>
            {
                string output = Path.Combine(Path.GetTempPath(), "KillerShell.Tests-" + Guid.NewGuid().ToString("N") + ".html");
                try
                {
                    var results = new List<SearchResult>
                    {
                        new SearchResult { FileName = "a.txt", Directory = @"C:\Data", SizeBytes = 1572864, Modified = new DateTime(2026, 11, 2, 14, 30, 5) },
                    };
                    HtmlExporter.Export(output, results, new List<SearchTerm>(), @"C:\Data");
                    string html = File.ReadAllText(output);
                    Assert.Contains("2026-11-02 14:30", html);
                    Assert.Contains("data-sort='20261102143005'", html);
                    Assert.Contains("1.5 MB", html);
                    Assert.DoesNotContain("2569", html);
                    Assert.Contains("<b>" + DateTime.Now.ToString("yyyy", CultureInfo.InvariantCulture), html);
                }
                finally { if (File.Exists(output)) File.Delete(output); }
            });
        }

        [Theory]
        [InlineData("th-TH")]
        [InlineData("de-DE")]
        public void StorageHtmlExport_WritesTheGeneratedYearInvariant(string culture)
        {
            UnderCulture(culture, () =>
            {
                string output = Path.Combine(Path.GetTempPath(), "KillerShell.Tests-" + Guid.NewGuid().ToString("N") + ".html");
                try
                {
                    var root = new StorageReportNode("root", @"C:\root", 1572864, true);
                    var report = new StorageReport(@"C:\root", @"C:\root", 1572864, 0, 0, false, root);
                    StorageHtmlExporter.Export(output, report);
                    string html = File.ReadAllText(output);
                    Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), html);
                    Assert.Contains("1.5 MB", html);
                }
                finally { if (File.Exists(output)) File.Delete(output); }
            });
        }

        [Fact]
        public void SessionFilter_StoresTheSizeInvariantAndShowsItInTheUsersFormat()
        {
            UnderCulture("de-DE", () =>
            {
                var typed = new SearchFilter { FieldIndex = SearchFilter.FieldSize, SizeText = "1,5", UnitIndex = SearchFilter.UnitMb };
                string row = MainWindow.EncodeSessionFilter(typed);
                Assert.Equal("1.5", row.Split('~')[4]);

                var back = MainWindow.DecodeSessionFilter(row)!;
                Assert.Equal("1,5", back.SizeText);
                Assert.Equal(1572864, back.SizeBytes);
            });
        }

        [Fact]
        public void SessionFilter_StillLoadsASizeSavedInTheOldRegionalForm()
        {
            UnderCulture("de-DE", () =>
            {
                var back = MainWindow.DecodeSessionFilter("1~0~~~1%2C5~1")!;
                Assert.Equal("1,5", back.SizeText);
                Assert.Equal(1572864, back.SizeBytes);
            });
        }
    }
}
