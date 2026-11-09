using System;
using System.IO;
using KillerShell.Terminal;

namespace KillerShell.Services
{
    internal enum AddressTargetKind { Missing, Folder, Edit, Terminal, Associated }

    /// <summary>
    /// What a path typed into the address bar should DO. A folder is browsed, a program or
    /// script goes to a terminal in its folder, anything that reads as text opens in an editor
    /// tab, and the rest goes to its Windows association.
    /// </summary>
    /// <remarks>
    /// Typing a file's path used to only select it in its folder, so typing a Markdown file
    /// looked like nothing happened. Text is decided by content rather than a list of
    /// extensions: a .cs or a .json with no association should still open here, and a list
    /// would never be finished.
    /// </remarks>
    internal static class AddressTarget
    {
        // Programs and scripts go to a terminal with the command staged but NOT run - typing a
        // path should never execute something on its own, which is the same reason .bat and
        // .cmd only ever get an Edit verb from the association code (Associations.cs).
        private static readonly string[] TerminalExtensions = [".exe", ".com", ".bat", ".cmd", ".ps1"];

        // Matches the editor's own ceiling (EditorTabs.cs MaxEditBytes): bigger goes to the
        // association rather than to an Edit tab that would refuse it.
        private const long MaxEditBytes = 32L * 1024 * 1024;
        private const int SniffBytes = 8192;

        /// <summary>
        /// What a typed address means, given the folder the tab is showing.
        /// </summary>
        /// <remarks>
        /// Explorer's reading: <c>..</c> and <c>Downloads</c> are relative to the tab's folder,
        /// <c>D:</c> is that drive's root, <c>D:work</c> is a folder on its root, and
        /// <c>\Windows</c> is on the tab folder's own drive. Nothing is resolved against
        /// KillerShell's working directory, which is wherever it happened to start. A place
        /// sentinel such as This PC, or anything with a scheme, is passed through untouched,
        /// and so is a relative address on a tab that is not showing a real folder.
        /// </remarks>
        internal static string ResolveTyped(string typed, string tabFolder)
        {
            try { typed = Environment.ExpandEnvironmentVariables(typed); }
            catch { }

            try
            {
                if (typed.Length >= 2 && typed[1] == ':' && char.IsLetter(typed[0]))
                {
                    string rest = typed.Substring(2).TrimStart('\\', '/');
                    return Path.GetFullPath(char.ToUpperInvariant(typed[0]) + @":\" + rest);
                }
                if (typed.StartsWith(@"\\", StringComparison.Ordinal) || typed.StartsWith("//", StringComparison.Ordinal))
                    return typed;
                if (typed.IndexOf(':') >= 0) return typed;   // a place sentinel or a scheme

                bool folderBehind = tabFolder.Length > 0 && !tabFolder.StartsWith(":", StringComparison.Ordinal)
                    && Path.IsPathRooted(tabFolder);
                if (!folderBehind) return typed;

                if (typed.StartsWith(@"\", StringComparison.Ordinal) || typed.StartsWith("/", StringComparison.Ordinal))
                    return Path.GetFullPath(Path.Combine(Path.GetPathRoot(tabFolder), typed.TrimStart('\\', '/')));
                return Path.GetFullPath(Path.Combine(tabFolder, typed));
            }
            catch { return typed; }
        }

        internal static AddressTargetKind Classify(string path)
        {
            if (Directory.Exists(path)) return AddressTargetKind.Folder;
            if (!File.Exists(path))     return AddressTargetKind.Missing;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(TerminalExtensions, ext) >= 0) return AddressTargetKind.Terminal;
            if (TooBigToEdit(path)) return AddressTargetKind.Associated;
            if (Array.IndexOf(App.TextExtensions, ext) >= 0) return AddressTargetKind.Edit;

            return LooksLikeText(path) ? AddressTargetKind.Edit : AddressTargetKind.Associated;
        }

        private static bool TooBigToEdit(string path)
        {
            try { return new FileInfo(path).Length > MaxEditBytes; }
            catch { return false; }
        }

        // A NUL byte in the first 8 KB is the same test git uses to call a file binary. UTF-16
        // text trips it too, so a BOM is checked first and counts as text.
        private static bool LooksLikeText(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > MaxEditBytes) return false;

                var buf = new byte[(int)Math.Min(SniffBytes, fs.Length)];
                int n = fs.Read(buf, 0, buf.Length);
                if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true;
                return Array.IndexOf(buf, (byte)0, 0, n) < 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// The command typed into a fresh shell for <paramref name="fileName"/>, run from the
        /// file's own folder. Null where there is no safe way to say it (WSL, custom shells).
        /// </summary>
        internal static string? StagedCommand(TerminalShellKind shell, string fileName) => shell switch
        {
            TerminalShellKind.PowerShell => "& " + PowerShellText.Literal(@".\" + fileName),
            TerminalShellKind.Cmd        => "\"" + fileName + "\"",
            _                            => null,
        };
    }
}
