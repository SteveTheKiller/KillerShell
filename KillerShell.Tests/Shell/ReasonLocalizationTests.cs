using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

// ArchiveProvider reports failures as fixed English tokens, and the window translates them.
// Every token it can hand back has a string in the interface language, so an archive status
// never shows English on a German or Japanese KillerShell.
public sealed class ReasonLocalizationTests
{
    [Fact]
    public void EveryArchiveReasonTokenHasALocalizedString()
    {
        string repo = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(repo, "Services", "ArchiveProvider.cs"));
        var tokens = Regex.Matches(source, "error = \"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(tokens);

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = XDocument.Load(Path.Combine(repo, "Strings", "en-US.xaml")).Root!.Elements()
            .Select(e => (string?)e.Attribute(x + "Key")).ToHashSet(StringComparer.Ordinal);

        foreach (string token in tokens)
        {
            string? key = MainWindow.ReasonKey(token);
            Assert.True(key != null, "No localized string for archive reason \"" + token + "\".");
            Assert.Contains(key, keys);
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
