using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests
{
    public sealed class GeneratedParityTests
    {
        [Fact]
        public void Locales_HaveTheEnglishKeySet()
        {
            string repo = FindRepoRoot();
            string strings = Path.Combine(repo, "Strings");
            HashSet<string> expected = ReadKeys(Path.Combine(strings, "en-US.xaml"));

            foreach (string locale in Directory.GetFiles(strings, "*.xaml"))
                Assert.True(expected.SetEquals(ReadKeys(locale)),
                    Path.GetFileName(locale) + " does not match the English resource key set.");
        }

        [Fact]
        public void Themes_HaveOneDictionaryPerTheme()
        {
            string repo = FindRepoRoot();
            var files = new HashSet<string>(Directory.GetFiles(Path.Combine(repo, "Themes"), "*.xaml")
                .Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            var expected = new HashSet<string>(Enum.GetNames(typeof(Theme))
                .Select(name => name == nameof(Theme.SE98) ? "98SE" : name), StringComparer.OrdinalIgnoreCase);

            Assert.True(expected.SetEquals(files), "Theme enum and theme dictionaries are out of sync.");
        }

        [Fact]
        public void GeneratedShortcuts_MatchTheSourceTableAndLocales()
        {
            string repo = FindRepoRoot();
            string generated = Path.Combine(Path.GetTempPath(), "KillerShell.Tests-" + Guid.NewGuid().ToString("N") + ".js");
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" +
                        Path.Combine(repo, "shell-landing", "generate-shortcuts.ps1") +
                        "\" -Output \"" + generated + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                Assert.NotNull(process);
                Assert.True(process!.WaitForExit(30000), "Shortcut generator timed out.");
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(Normalize(File.ReadAllText(Path.Combine(repo, "shell-landing", "shortcuts.generated.js"))),
                    Normalize(File.ReadAllText(generated)));
            }
            finally
            {
                if (File.Exists(generated)) File.Delete(generated);
            }
        }

        // A promoted touch press, or a click whose button is already up by the time the handler
        // runs, makes Window.DragMove throw InvalidOperationException, and an unhandled throw
        // there takes the whole app down. Every title bar goes through DialogChrome.Drag, which
        // checks the button and swallows that throw.
        [Fact]
        public void TitleBarDrags_AreGuarded()
        {
            string repo = FindRepoRoot();
            var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DialogChrome.cs", "AltDrag.cs" };
            var unguarded = new List<string>();
            foreach (string file in Directory.GetFiles(repo, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(repo.Length + 1);
                if (relative.StartsWith("KillerShell.Tests", StringComparison.OrdinalIgnoreCase)) continue;
                if (relative.Split('\\').Any(part => part is "bin" or "obj")) continue;
                if (exempt.Contains(Path.GetFileName(file))) continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains("DragMove()") && !lines[i].Contains("try"))
                        unguarded.Add(relative + ":" + (i + 1));
            }
            Assert.True(unguarded.Count == 0, "Unguarded DragMove: " + string.Join(", ", unguarded));
        }

        private static HashSet<string> ReadKeys(string path)
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            return XDocument.Load(path).Root!.Elements()
                .Select(element => (string?)element.Attribute(x + "Key"))
                .Where(key => !string.IsNullOrEmpty(key))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string Normalize(string value) => value.Replace("\r\n", "\n");

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("KillerShell repository root not found.");
        }
    }
}
