using System;
using System.Collections.Generic;
using System.Linq;

namespace KillerShell.Services
{
    /// <summary>
    /// The saved favorites setting. Every saved place stays in the drawer in its saved order; the
    /// ones that cannot be reached right now (an offline share, an unplugged drive) are marked so
    /// the drawer can show them dimmed, and they are saved back like any other.
    /// </summary>
    internal static class SavedBookmarks
    {
        internal const char Separator = '|';

        internal sealed class Loaded
        {
            internal Loaded(IReadOnlyList<string> paths, IReadOnlyList<string> unreachable)
            {
                Paths = paths;
                Unreachable = unreachable;
            }

            internal IReadOnlyList<string> Paths { get; }
            internal IReadOnlyList<string> Unreachable { get; }
        }

        internal static Loaded Parse(string saved, Func<string, bool> reachable)
        {
            var paths = saved.Split([Separator], StringSplitOptions.RemoveEmptyEntries).ToList();
            var unreachable = paths.Where(path => { try { return !reachable(path); } catch { return true; } }).ToList();
            return new Loaded(paths, unreachable);
        }

        internal static string Serialize(IEnumerable<string> paths)
            => string.Join(Separator.ToString(), paths);
    }
}
