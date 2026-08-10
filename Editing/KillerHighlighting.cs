using System;
using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

// The definitions AvalonEdit does not ship, registered alongside the ones it does.
//
// Its built-in set is a list of programming languages, which is a fine set for an IDE and the
// wrong one for this app. The files a field tech opens are .bat, .reg, .ini, .yml, .log and
// .csv, and every one of them arrived as flat gray text - which reads as the editor being
// broken rather than as a language it has not heard of.
//
// Registered INTO the shared HighlightingManager rather than resolved separately, so
// GetDefinitionByExtension stays the single place an extension turns into colors and the theme
// pass (EditorHighlighting.cs) reaches these the same way it reaches PowerShell's.
namespace KillerShell.Editing
{
    internal static class KillerHighlighting
    {
        private const string Prefix = "KillerShell.Highlighting.";

        private static bool _done;

        /// <summary>
        /// Register them once. Safe to call on every editor construction.
        /// </summary>
        /// <remarks>
        /// Lazy rather than wired into startup: nothing needs these until the first file is
        /// opened for editing, and a launch that parses six XML documents to find out whether
        /// anybody wanted them is a launch that got slower for nothing.
        ///
        /// A failure here is swallowed per definition rather than being allowed to take the rest
        /// down with it: a bad regex in one .xshd should cost that one format its colors, not
        /// stop the editor opening.
        /// </remarks>
        internal static void EnsureRegistered()
        {
            if (_done) return;
            _done = true;

            Add("Batch",    [".bat", ".cmd"],                                           "Batch.xshd");
            Add("Registry", [".reg"],                                                   "Registry.xshd");
            Add("Ini",      [".ini", ".conf", ".cfg", ".inf", ".properties", ".env"],   "Ini.xshd");
            Add("Yaml",     [".yml", ".yaml"],                                          "Yaml.xshd");
            Add("Log",      [".log", ".out", ".err", ".trace"],                         "Log.xshd");
            Add("Csv",      [".csv", ".tsv"],                                           "Csv.xshd");
            AddBuiltIn("MarkDown", [".md", ".markdown"]);
            AddBuiltIn("JSON", [".json"]);
            AddBuiltIn("XML", [".xml", ".config", ".xaml"]);
            AddBuiltIn("Python", [".py"]);
            AddBuiltIn("C#", [".cs"]);
        }

        private static void Add(string name, string[] extensions, string resource)
        {
            try
            {
                using Stream? stream = typeof(KillerHighlighting).Assembly
                    .GetManifestResourceStream(Prefix + resource);
                if (stream == null) return;

                using var reader = new XmlTextReader(stream);

                // HighlightingManager.Instance as the resolver, so a definition here could refer
                // to a built-in one by name later without this needing to change.
                var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                HighlightingManager.Instance.RegisterHighlighting(name, extensions, definition);
            }
            catch { /* one format loses its colors; the editor still opens the file */ }
        }

        /// <summary>Register a built-in AvalonEdit highlighting definition by name.</summary>
        private static void AddBuiltIn(string definitionName, string[] extensions)
        {
            try
            {
                var definition = HighlightingManager.Instance.GetDefinition(definitionName);
                if (definition != null)
                    HighlightingManager.Instance.RegisterHighlighting(definitionName, extensions, definition);
            }
            catch { /* definition not found or registration failed */ }
        }
    }
}
