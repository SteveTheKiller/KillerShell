using System;
using System.Collections.Generic;
using System.Linq;

namespace KillerShell.Services
{
    /// <summary>
    /// The saved favorites setting. Every saved place stays in the drawer in its saved order; the
    /// ones that cannot be reached right now (an offline share, an unplugged drive) are found off
    /// the UI thread (Live) so the drawer can show them dimmed, and they are saved back like any
    /// other.
    /// </summary>
    internal static class SavedBookmarks
    {
        internal const char Separator = '|';

        /// <summary>The saved places in order. Nothing is checked here: the setting is read while
        /// the main window is built, and one dead share would hold the window back for the whole
        /// network timeout.</summary>
        internal static IReadOnlyList<string> Paths(string saved)
            => saved.Split([Separator], StringSplitOptions.RemoveEmptyEntries).ToList();

        /// <summary>
        /// Which favorites are live, in order. A WSL path is answered by its own distribution, so
        /// it counts as live without being handed to <paramref name="diskCheck"/>. Keeping it out
        /// matters because Reachability shares a running check between callers, and the recents
        /// menu's Directory.Exists of the same folder can wake a stopped distribution for longer
        /// than the drawer's budget.
        /// </summary>
        internal static List<string> Live(IReadOnlyList<string> paths, Func<IReadOnlyList<string>, List<string>> diskCheck)
        {
            var onDisk = paths.Where(path => !WslDistributions.TryParsePath(path, out _, out _)).ToList();
            var live = new HashSet<string>(onDisk.Count == 0 ? [] : diskCheck(onDisk), StringComparer.OrdinalIgnoreCase);
            return paths.Where(path => !onDisk.Contains(path) || live.Contains(path)).ToList();
        }

        internal static string Serialize(IEnumerable<string> paths)
            => string.Join(Separator.ToString(), paths);
    }
}
