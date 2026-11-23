using System;
using System.Globalization;
using System.IO;
using System.Linq;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// Every language has four homes: the Locale enum, the Strings file, the culture map and the
// picker row in Shell/Language.cs. A new language that misses one of them silently falls back
// to English, so these checks tie the four together.
public sealed class LocaleWiringTests
{
    [Fact]
    public void EveryLocaleHasAStringsFileAndCulture()
    {
        string strings = Path.Combine(FindRepoRoot(), "Strings");
        foreach (Locale locale in Enum.GetValues(typeof(Locale)))
        {
            string? file = LocaleManager.FileFor(locale);
            if (locale == Locale.EnUS) { Assert.Null(file); continue; }
            Assert.NotNull(file);
            Assert.True(File.Exists(Path.Combine(strings, file + ".xaml")), locale + " has no Strings/" + file + ".xaml");
            Assert.NotEqual("en-US", LocaleManager.CultureFor(locale).Name);
        }
    }

    [Fact]
    public void EveryStringsFileHasALocale()
    {
        string strings = Path.Combine(FindRepoRoot(), "Strings");
        var codes = Enum.GetValues(typeof(Locale)).Cast<Locale>()
            .Select(l => LocaleManager.FileFor(l) ?? "en-US")
            .ToList();
        foreach (string path in Directory.GetFiles(strings, "*.xaml"))
            Assert.Contains(Path.GetFileNameWithoutExtension(path), codes);
    }

    [Fact]
    public void EveryLocaleHasAPickerRow()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Shell", "Language.cs"));
        foreach (Locale locale in Enum.GetValues(typeof(Locale)))
        {
            string code = LocaleManager.FileFor(locale) ?? "en-US";
            Assert.Contains("(Locale." + locale + ",", source);
            Assert.Contains("\"" + code + "\")", source);
        }
    }

    [Theory]
    [InlineData("sv-SE", "SvSE")]
    [InlineData("sv-FI", "SvSE")]
    [InlineData("ko-KR", "KoKR")]
    [InlineData("ja-JP", "JaJP")]
    [InlineData("sl-SI", "SlSI")]
    public void WindowsDisplayLanguageMapsToTheLocale(string culture, string expected)
    {
        Assert.Equal(expected, LocaleManager.LocaleForCulture(CultureInfo.GetCultureInfo(culture)).ToString());
    }

    [Fact]
    public void KoreanFallsBackToMalgunGothic()
    {
        Assert.Contains("Malgun Gothic", UiFontStack.Fallbacks("ko-KR"));
        Assert.DoesNotContain("Malgun Gothic", UiFontStack.Fallbacks("sv-SE"));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerShell repository root not found.");
    }
}
