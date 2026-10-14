using System;
using System.Collections.Generic;
using System.Linq;

namespace KillerShell.Services
{
    /// <summary>
    /// Nicknames for files and folders: a label the user gives an item without renaming it on
    /// disk. Shown as "Nickname (real name)" wherever the item is drawn - the folder tree, the
    /// saved places and every view of the listing.
    /// </summary>
    /// <remarks>
    /// Keyed by full path, case-insensitive, and stored as one setting: "path TAB nickname"
    /// lines. Windows paths cannot hold a tab or a line break, and a nickname has both stripped
    /// as it is saved, so neither separator ever needs escaping.
    /// --demo keeps nicknames in memory only: it never reads the real ones (they would show the
    /// machine's own labels in a screenshot) and never writes over them.
    /// </remarks>
    internal static class Nicknames
    {
        private const string SettingKey = "Nicknames";

        private static Dictionary<string, string>? _map;

        /// <summary>Raised with the item's path after its nickname is set or removed.</summary>
        internal static event Action<string>? Changed;

        private static Dictionary<string, string> Map
        {
            get
            {
                if (_map != null) return _map;
                _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Shell.MainWindow.DemoMode) return _map;

                string? saved = ThemeManager.GetSetting(SettingKey);
                if (string.IsNullOrEmpty(saved)) return _map;
                foreach (string line in saved!.Split('\n'))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string nick = Clean(line[(tab + 1)..]);
                    if (nick.Length > 0) _map[Key(line[..tab])] = nick;
                }
                return _map;
            }
        }

        // A trailing backslash is dropped so "C:\Tools" and "C:\Tools\" are one item, except on
        // a drive root, where "C:\" is the path and "C:" would mean something else.
        private static string Key(string path)
        {
            string p = path.Trim();
            return p.Length > 3 ? p.TrimEnd('\\') : p;
        }

        private static string Clean(string? nickname)
            => new string([.. (nickname ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c)]).Trim();

        /// <summary>The item's nickname, or an empty string when it has none.</summary>
        internal static string Get(string? path)
        {
            if (string.IsNullOrEmpty(path) || Map.Count == 0) return string.Empty;
            return Map.TryGetValue(Key(path!), out var nick) ? nick : string.Empty;
        }

        /// <summary>The label to draw: "Nickname (name)", or just the name without one.</summary>
        internal static string Label(string? path, string name)
        {
            string nick = Get(path);
            return nick.Length == 0 ? name : nick + " (" + name + ")";
        }

        /// <summary>Sets the nickname, or removes it when the text is empty.</summary>
        internal static void Set(string path, string? nickname)
        {
            if (string.IsNullOrEmpty(path)) return;
            string key = Key(path), nick = Clean(nickname);
            if (nick.Length == 0) { if (!Map.Remove(key)) return; }
            else
            {
                if (Map.TryGetValue(key, out var old) && old == nick) return;
                Map[key] = nick;
            }
            Save();
            Changed?.Invoke(path);
        }

        /// <summary>
        /// Carries nicknames along when an item is renamed or moved inside KillerShell: the item
        /// itself and, for a folder, everything under it.
        /// </summary>
        internal static void Move(string oldPath, string newPath)
        {
            if (Map.Count == 0) return;
            string from = Key(oldPath), to = Key(newPath);
            string prefix = from + "\\";
            var moved = Map.Keys.Where(k => k.Equals(from, StringComparison.OrdinalIgnoreCase)
                                         || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                .ToList();
            if (moved.Count == 0) return;
            foreach (string k in moved)
            {
                string nick = Map[k];
                Map.Remove(k);
                Map[to + k[from.Length..]] = nick;
            }
            Save();
            Changed?.Invoke(newPath);
        }

        private static void Save()
        {
            if (Shell.MainWindow.DemoMode) return;
            ThemeManager.SetSetting(SettingKey,
                string.Join("\n", Map.Select(kv => kv.Key + "\t" + kv.Value)));
        }
    }
}
