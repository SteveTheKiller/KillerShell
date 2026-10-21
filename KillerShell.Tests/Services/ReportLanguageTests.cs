using System;
using System.Globalization;
using System.IO;
using System.Linq;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ReportLanguageTests
{
    [Fact]
    public void EveryLocaleHasItsOwnValidLanguageTag()
    {
        var locales = Enum.GetValues(typeof(Locale)).Cast<Locale>().ToArray();
        var tags = locales.Select(LocaleManager.LanguageTag).ToArray();

        Assert.Equal(locales.Length, tags.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var (locale, tag) in locales.Zip(tags, (l, t) => (l, t)))
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            Assert.False(string.IsNullOrEmpty(culture.Name), locale + " has no culture");
            if (locale != Locale.EnUS) Assert.NotEqual("en", culture.TwoLetterISOLanguageName);
        }
        Assert.Equal("ja-JP", LocaleManager.LanguageTag(Locale.Ja));
        Assert.Equal("en-US", LocaleManager.LanguageTag(Locale.EnUS));
    }

    [Theory]
    [InlineData("html")]
    [InlineData("storage")]
    public void BothReportsDeclareTheCurrentLanguage(string format)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-report-lang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "report.html");
        try
        {
            var results = new[] { new SearchResult { FileName = "a.txt", FilePath = "C:\\data\\a.txt", Directory = "C:\\data" } };
            if (format == "html") HtmlExporter.Export(path, results, Array.Empty<SearchTerm>(), "C:\\data");
            else
            {
                var node = new StorageReportNode("data", "C:\\data", 1, true);
                node.Children.Add(new StorageReportNode("a.txt", "C:\\data\\a.txt", 1, false));
                StorageHtmlExporter.Export(path, new StorageReport("C:\\data", "C:\\data", 1, 0, 0, false, node));
            }
            string expected = "lang='" + LocaleManager.LanguageTag(LocaleManager.Current) + "'";
            Assert.Contains(expected, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
