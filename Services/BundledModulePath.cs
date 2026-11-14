using System;
using System.Collections.Generic;
using System.IO;

namespace KillerShell.Services
{
    /// <summary>
    /// Where KillerShell's own modules sit on PSModulePath: FIRST, so the KillerScripts and
    /// KillerPivot that ship with this version are the ones every terminal tab runs, even when an
    /// older copy is installed from PSGallery. PowerShell takes the first folder that has a module,
    /// and Windows PowerShell keeps the inherited order. PowerShell 7 inserts its standard
    /// folders at startup, so the terminal startup command restores the bundled root first.
    /// </summary>
    internal static class BundledModulePath
    {
        internal static string WithBundledFirst(string current, string root)
        {
            string bundled = Normalize(root);
            string family = Normalize(Path.GetDirectoryName(bundled) ?? bundled);
            var entries = new List<string> { root };
            foreach (string entry in (current ?? string.Empty).Split(';'))
            {
                if (entry.Trim().Length == 0) continue;
                string normalized = Normalize(entry);
                if (string.Equals(normalized, bundled, StringComparison.OrdinalIgnoreCase)) continue;
                // Another KillerShell version's unpacked modules (…\KillerShell\Modules\<version>)
                // must not stand behind this one and answer for a module this version dropped.
                if (string.Equals(Normalize(Path.GetDirectoryName(normalized) ?? string.Empty), family, StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(entry);
            }
            return string.Join(";", entries);
        }

        /// <summary>
        /// The PowerShell statement the terminal startup command runs inside the session: the
        /// bundled root goes first, and any copy already on the path is removed, so running it
        /// again (after $PROFILE, or in a nested pwsh that inherited the path) never adds a
        /// second entry. It stays needed because PowerShell 7 inserts its own folders ahead of
        /// the inherited path at startup.
        /// </summary>
        internal static string PowerShellSnippet(string root)
        {
            string quoted = root.Replace("'", "''");
            return "$ksRoot = '" + quoted + "'; " +
                   "$env:PSModulePath = (@($ksRoot) + @($env:PSModulePath -split ';' | " +
                   "Where-Object { $_.Trim() -and $_.Trim().TrimEnd('\\', '/') -ine $ksRoot.TrimEnd('\\', '/') })) -join ';'; ";
        }

        private static string Normalize(string path) => path.Trim().TrimEnd('\\', '/');
    }
}
