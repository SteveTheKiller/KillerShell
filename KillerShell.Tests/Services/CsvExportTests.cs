using System;
using System.Collections.Generic;
using System.Linq;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    /// <summary>
    /// The results CSV is meant to be opened in a spreadsheet. A file name such as =1+1 is
    /// legal, and a spreadsheet would run it as a formula, so text cells that a spreadsheet would
    /// read as one start with an apostrophe. Quoting alone does not stop that.
    /// </summary>
    public sealed class CsvExportTests
    {
        [Theory]
        [InlineData("=1+1")]
        [InlineData("+2+3")]
        [InlineData("-2+3")]
        [InlineData("@SUM(1,2)")]
        [InlineData("\t=1+1")]
        [InlineData("\r=1+1")]
        [InlineData("  =1+1")]
        [InlineData("\uFF1D1+1")]
        public void FormulaLeadingNames_AreWrittenAsLiteralText(string name)
        {
            string[] cells = Row(new SearchResult { FileName = name, Directory = @"C:\Data" });
            Assert.Equal("\"'" + name.Replace("\"", "\"\"") + "\"", cells[0]);
        }

        [Fact]
        public void FormulaLeadingFolder_IsWrittenAsLiteralText()
        {
            string[] cells = Row(new SearchResult { FileName = "a.txt", Directory = "=cmd|' /C calc'!A0" });
            Assert.Equal("\"'=cmd|' /C calc'!A0\"", cells[1]);
        }

        [Theory]
        [InlineData("notes.txt")]
        [InlineData("report 2026.xlsx")]
        [InlineData("\u65e5\u672c\u8a9e.txt")]
        [InlineData("a=b.txt")]
        [InlineData("x-1.log")]
        public void OrdinaryNames_AreUnchanged(string name)
        {
            string[] cells = Row(new SearchResult { FileName = name, Directory = @"C:\Data" });
            Assert.Equal("\"" + name + "\"", cells[0]);
        }

        [Fact]
        public void QuotesAndCommas_StayQuotedAndEscaped()
        {
            string csv = CsvExport.Build(new[] { new SearchResult { FileName = "say \"hi\", ok.txt", Directory = @"C:\Data" } });
            string row = csv.Split(new[] { "\r\n" }, StringSplitOptions.None)[1];
            Assert.StartsWith("\"say \"\"hi\"\", ok.txt\",", row);
        }

        [Fact]
        public void SizeStaysANumber()
        {
            string[] cells = Row(new SearchResult { FileName = "-big.bin", Directory = @"C:\Data", SizeBytes = 1234567 });
            Assert.Equal("1234567", cells[2]);
        }

        [Fact]
        public void HeaderIsUnchanged()
        {
            string csv = CsvExport.Build(Array.Empty<SearchResult>());
            Assert.Equal("Name,Folder,SizeBytes,Modified,Found\r\n", csv);
        }

        // Splits the single data row on the commas between quoted cells. The fixtures keep commas
        // out of every cell except where the test says otherwise.
        private static string[] Row(SearchResult result)
        {
            string csv = CsvExport.Build(new[] { result });
            string row = csv.Substring(csv.IndexOf("\r\n", StringComparison.Ordinal) + 2);
            row = row.Substring(0, row.Length - 2);
            var cells = new List<string>();
            int i = 0;
            while (i < row.Length)
            {
                int start = i;
                if (row[i] == '"')
                {
                    i++;
                    while (i < row.Length && !(row[i] == '"' && (i + 1 == row.Length || row[i + 1] != '"'))) i += row[i] == '"' ? 2 : 1;
                    i++;
                }
                else while (i < row.Length && row[i] != ',') i++;
                cells.Add(row.Substring(start, i - start));
                i++;
            }
            return cells.ToArray();
        }
    }
}
