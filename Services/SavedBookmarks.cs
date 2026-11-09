using System;
using System.Collections.Generic;
using System.Linq;

namespace KillerShell.Services
{
    /// <summary>
    /// The saved favorites setting, split into the rows the drawer shows and the ones it holds back.
    /// </summary>
    internal static class SavedBookmarks
    {
        internal const char Separator = '|';

        internal sealed class Loaded
        {
            internal Loaded(IReadOnlyList<string> visible, IReadOnlyList<string> held)
            {
                Visible = visible;
                Held = held;
            }

            internal IReadOnlyList<string> Visible { get; }
            internal IReadOnlyList<string> Held { get; }
        }

        internal static Loaded Parse(string saved, Func<string, bool> reachable)
        {
            var visible = new List<string>();
            var held = new List<string>();
            foreach (string path in saved.Split([Separator], StringSplitOptions.RemoveEmptyEntries))
                (reachable(path) ? visible : held).Add(path);
            return new Loaded(visible, held);
        }

        /// <summary>
        /// The drawer's rows in order, then every held favorite the drawer does not already show.
        /// A held one goes to the end: it is invisible, so its old position means nothing to anyone.
        /// </summary>
        internal static string Serialize(IEnumerable<string> visible, IEnumerable<string> held)
        {
            var rows = visible.ToList();
            var seen = new HashSet<string>(rows.Select(Key), StringComparer.OrdinalIgnoreCase);
            foreach (string path in held)
                if (seen.Add(Key(path))) rows.Add(path);
            return string.Join(Separator.ToString(), rows);
        }

        private static string Key(string path) => path.TrimEnd('\\', '/');
    }
}
