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

        /// <summary>How long one root may take to answer whether it exists.</summary>
        internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The candidates that answer <paramref name="exists"/> with true within
        /// <paramref name="timeout"/>, probed in parallel and off the caller's thread, in their
        /// original order. A disconnected mapped drive, an empty optical drive or an unreachable
        /// share can take many seconds to say no; probing them on the UI thread froze the whole
        /// window on Search. A probe still running at the deadline is abandoned and its root
        /// left out, the same as one that answered no.
        /// </summary>
        internal static System.Threading.Tasks.Task<List<string>> ProbeAsync(IReadOnlyList<string> candidates,
            Func<string, bool> exists, TimeSpan timeout)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                var probes = candidates.Select(path => System.Threading.Tasks.Task.Run(() =>
                {
                    try { return exists(path); }
                    catch { return false; }
                })).ToArray();
                System.Threading.Tasks.Task.WaitAll(probes, timeout);
                var found = new List<string>();
                for (int i = 0; i < probes.Length; i++)
                    if (probes[i].Status == System.Threading.Tasks.TaskStatus.RanToCompletion && probes[i].Result)
                        found.Add(candidates[i]);
                return found;
            });
        }

        private static bool Contains(string parent, string child) =>
            string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
