using System;
using System.Collections.Generic;

namespace KillerShell.Services
{
    /// <summary>Text edited during one demo session. All operations stay in memory.</summary>
    internal static class DemoTextStore
    {
        private static readonly Dictionary<string, string> Documents = new(StringComparer.OrdinalIgnoreCase);

        internal static bool Contains(string path) => Documents.ContainsKey(path);

        internal static string Open(string path, string initialText)
        {
            if (Documents.TryGetValue(path, out string saved)) return saved;
            Documents[path] = initialText;
            return initialText;
        }

        internal static bool TrySave(string path, string text)
        {
            if (!Documents.ContainsKey(path)) return false;
            Documents[path] = text;
            return true;
        }
    }
}
