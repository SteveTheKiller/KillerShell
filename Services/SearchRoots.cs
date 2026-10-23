using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KillerShell.Services
{
    internal static class SearchRoots
    {
        internal static List<string> Normalize(IEnumerable<string> paths)
        {
            var roots = new List<string>();
            foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                string full;
                try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
                catch (ArgumentException) { continue; }
                catch (NotSupportedException) { continue; }
                catch (PathTooLongException) { continue; }
                if (full.Length == 2 && full[1] == ':') full += Path.DirectorySeparatorChar;
                if (roots.Any(root => Contains(root, full))) continue;
                roots.RemoveAll(root => Contains(full, root));
                roots.Add(full);
            }
            return roots;
        }

        private static bool Contains(string parent, string child) =>
            string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
