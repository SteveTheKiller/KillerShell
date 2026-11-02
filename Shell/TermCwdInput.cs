using System;
using System.IO;
using KillerShell.Terminal;

namespace KillerShell.Shell
{
    internal enum TermCwdKind { Ignore, NotFound, File, Folder }

    internal readonly struct TermCwdTarget
    {
        internal TermCwdTarget(TermCwdKind kind, string path) { Kind = kind; Path = path; }
        internal TermCwdKind Kind { get; }
        internal string Path { get; }
    }

    /// <summary>What text typed into a terminal tab's location box means for that tab's shell.</summary>
    internal static class TermCwdInput
    {
        internal static TermCwdTarget Resolve(string typed, TerminalProfile profile, string currentFolder)
            => Resolve(typed, profile, currentFolder, File.Exists, Directory.Exists);

        internal static TermCwdTarget Resolve(string typed, TerminalProfile profile, string currentFolder,
            Func<string, bool> fileExists, Func<string, bool> folderExists)
        {
            string path = typed.Trim();
            // A WSL tab takes the text as the distribution's own path; the Windows checks below do not apply.
            if (profile.Kind == TerminalShellKind.Wsl)
                return HasLineBreak(path) ? new TermCwdTarget(TermCwdKind.Ignore, path) : new TermCwdTarget(TermCwdKind.Folder, path);

            string resolved;
            try
            {
                string unquoted = path.Trim('"');
                // A relative name is relative to the tab's folder, not to the folder this app runs from.
                resolved = Path.GetFullPath(Path.IsPathRooted(unquoted) ? unquoted : Path.Combine(currentFolder, unquoted));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new TermCwdTarget(TermCwdKind.NotFound, path);
            }

            // A file is something to open, not somewhere to go.
            if (fileExists(resolved))
                return new TermCwdTarget(HasLineBreak(resolved) ? TermCwdKind.Ignore : TermCwdKind.File, resolved);
            if (!folderExists(resolved)) return new TermCwdTarget(TermCwdKind.NotFound, path);
            return new TermCwdTarget(HasLineBreak(resolved) ? TermCwdKind.Ignore : TermCwdKind.Folder, resolved);
        }

        // The text goes to the shell as a typed command, so a line break would end the command early.
        private static bool HasLineBreak(string path) => path.IndexOfAny(['\r', '\n', '\0']) >= 0;
    }
}
