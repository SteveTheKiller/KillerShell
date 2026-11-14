using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerShell.Tests
{
    public sealed class TerminalTooltipConsistencyTests
    {
        // The terminal bar's tooltips name their chord after the label, so within one language they
        // share the separator and the local modifier names (Strg+Umschalt, Ctrl+Mayús, Ctrl+Maj).
        // Clear scrollback and Open this folder are both Ctrl+Shift chords and must read alike.
        [Fact]
        public void ClearScrollbackTooltipMatchesItsSiblingInEveryLanguage()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var chord = new Regex(@"^(?<label>.*?)(?<gap>\s+)\((?<mods>.+)\+(?<key>[^+)]+)\)$");
            foreach (var file in Directory.GetFiles(Path.Combine(Root(), "Strings"), "*.xaml"))
            {
                var strings = XDocument.Load(file).Root!.Elements()
                    .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value);
                var folder = chord.Match(strings["Str_TT_TermFolder"]);
                var clear = chord.Match(strings["Str_TT_TermClear"]);
                string name = Path.GetFileName(file);
                Assert.True(folder.Success && clear.Success, name + " tooltips must end with a chord in parentheses.");
                Assert.Equal(folder.Groups["gap"].Value, clear.Groups["gap"].Value);
                Assert.Equal(folder.Groups["mods"].Value, clear.Groups["mods"].Value);
                Assert.Equal("L", clear.Groups["key"].Value);
                Assert.Equal(strings["Str_Term_Clear"], clear.Groups["label"].Value);
            }
        }

        private static string Root()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "KillerShell.csproj"))) return dir.FullName;
            throw new InvalidOperationException("KillerShell.csproj was not found above the test output.");
        }
    }
}
