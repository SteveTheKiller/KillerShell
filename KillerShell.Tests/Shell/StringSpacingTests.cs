using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using Xunit;

namespace KillerShell.Tests.Shell;

// XAML collapses a run of spaces inside a string resource unless the element asks to keep it.
// The Storage scan counts are joined to the size with two spaces in code, so the counts'
// own two-space separator has to survive loading in every language, or the status line
// reads "1 folders 2 files  48 B" with its parts run together.
public sealed class StringSpacingTests
{
    [Fact]
    public void TheScanCountsKeepTheirTwoSpaceSeparatorInEveryLanguage()
    {
        string folder = Path.Combine(FindRepoRoot(), "Strings");
        foreach (string file in Directory.GetFiles(folder, "*.xaml"))
        {
            var loaded = (ResourceDictionary)XamlReader.Parse(File.ReadAllText(file));
            string counts = (string)loaded["Str_Storage_ScanCounts"];
            Assert.True(counts.Contains("  "), Path.GetFileName(file) + " loads the scan counts as \"" + counts + "\".");
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerShell repository root not found.");
    }
}
