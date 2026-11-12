using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KillerShell.Services
{
    /// <summary>
    /// The sort each browsed folder was last given, so a folder you sorted by name opens by name
    /// again while every other folder opens newest first.
    /// </summary>
    /// <remarks>
    /// One setting value holds the whole map, one folder per line as "index|asc|path", most
    /// recently set first and capped at <see cref="Capacity"/> so it cannot grow without bound.
    /// A folder set back to the default (Modified, newest first) is dropped rather than stored,
    /// because the default is what an unknown folder gets anyway. The value is read and written
    /// fresh on every use, so windows open side by side share one map.
    /// </remarks>
    internal sealed class FolderSortMemory
    {
        internal const int Capacity = 500;
        internal const int DefaultIndex = 4;      // Modified
        internal const bool DefaultAsc = false;   // newest first

        private readonly string _setting;
        private readonly Func<string, string?> _read;
        private readonly Action<string, string> _write;

        internal FolderSortMemory(string setting, Func<string, string?> read, Action<string, string> write)
        {
            _setting = setting;
            _read = read;
            _write = write;
        }

        /// <summary>The folder's saved sort, or Modified newest first when it has none.</summary>
        internal (int Index, bool Asc) For(string folder)
        {
            string key = Normalize(folder);
            foreach (var entry in Load())
                if (string.Equals(entry.Folder, key, StringComparison.OrdinalIgnoreCase))
                    return (entry.Index, entry.Asc);
            return (DefaultIndex, DefaultAsc);
        }

        internal void Remember(string folder, int index, bool asc)
        {
            // "As found" is a search order with nothing behind it in a folder listing.
            if (index < 1 || index > 6 || string.IsNullOrWhiteSpace(folder)) return;
            string key = Normalize(folder);

            var entries = Load().Where(e => !string.Equals(e.Folder, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (index != DefaultIndex || asc != DefaultAsc) entries.Insert(0, (key, index, asc));
            if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);

            _write(_setting, string.Join("\n", entries.Select(e =>
                e.Index.ToString(CultureInfo.InvariantCulture) + "|" + (e.Asc ? "1" : "0") + "|" + e.Folder)));
        }

        private List<(string Folder, int Index, bool Asc)> Load()
        {
            var entries = new List<(string, int, bool)>();
            string? raw = _read(_setting);
            if (string.IsNullOrEmpty(raw)) return entries;

            foreach (string line in raw!.Split('\n'))
            {
                // A path can hold neither '|' nor a newline, so the third field is the whole rest.
                var parts = line.Split(new[] { '|' }, 3);
                if (parts.Length != 3 || parts[2].Length == 0) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    || index < 1 || index > 6) continue;
                entries.Add((parts[2], index, parts[1] == "1"));
            }
            return entries;
        }

        /// <summary>
        /// One spelling per folder: a trailing separator is dropped, except on a drive root,
        /// where "C:" alone would mean the drive's current directory rather than its root.
        /// </summary>
        internal static string Normalize(string folder)
        {
            string trimmed = folder.Trim();
            string bare = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (bare.Length == 2 && bare[1] == ':') return bare + Path.DirectorySeparatorChar;
            return bare.Length == 0 ? trimmed : bare;
        }
    }
}
