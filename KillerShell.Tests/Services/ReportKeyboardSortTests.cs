using System;
using System.IO;
using System.Text.RegularExpressions;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ReportKeyboardSortTests
{
    [Fact]
    public void EverySortableHeaderIsAButtonWithASortState()
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-report-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "report.html");
        try
        {
            var results = new[] { new SearchResult { FileName = "a.txt", FilePath = "C:\\data\\a.txt", Directory = "C:\\data" } };
            HtmlExporter.Export(path, results, Array.Empty<SearchTerm>(), "C:\\data");
            string html = File.ReadAllText(path);
            string head = Regex.Match(html, "<thead>.*?</thead>", RegexOptions.Singleline).Value;

            Assert.Equal(5, Regex.Matches(head, "<th [^>]*aria-sort='none'><button type='button' class='sortbtn'>").Count);
            Assert.Equal(5, Regex.Matches(head, "<span class='arrow' aria-hidden='true'>").Count);
            // Sorting hangs off the buttons, so Enter and Space work, and the state is announced.
            Assert.Contains("getElementsByTagName('button')[0].onclick", html);
            Assert.Contains("setAttribute('aria-sort',sortDir>0?'ascending':'descending')", html);
            Assert.Contains(".sortbtn:focus-visible", html);
            Assert.DoesNotContain("ths[i].onclick", html);
        }
        finally { Directory.Delete(root, true); }
    }
}
