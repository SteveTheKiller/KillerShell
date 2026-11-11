using System;
using System.IO;

namespace KillerShell.Services
{
    internal enum EditorPathAction { Unchanged, Open, Rename, NotFound, Exists }

    internal readonly record struct EditorPathResult(EditorPathAction Action, string Path);

    /// <summary>
    /// What a path typed into the editor bar means. Another file that already exists opens in the
    /// editor; a name nothing has yet renames (or, for an untitled document, saves) the current one.
    /// A bare name resolves against the document's own folder, so ".env" means the .env beside it.
    /// </summary>
    internal static class EditorPathEntry
    {
        internal static EditorPathResult Resolve(string typed, string currentFile, string folder,
                                                 Func<string, bool> fileExists, Func<string, bool> folderExists)
        {
            string text = (typed ?? string.Empty).Trim().Trim('"').Trim();
            if (text.Length == 0) return new(EditorPathAction.Unchanged, currentFile);

            string path = text;
            try
            {
                if (!Path.IsPathRooted(text) && folder.Length > 0) path = Path.Combine(folder, text);
                path = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new(EditorPathAction.NotFound, text);
            }

            bool untitled = currentFile.Length == 0;
            if (!untitled && string.Equals(path, currentFile, StringComparison.Ordinal))
                return new(EditorPathAction.Unchanged, path);

            // A case-only change of the current file is a rename. On a case-insensitive volume the
            // exists check below would find the very file being renamed and open it again.
            if (!untitled && string.Equals(path, currentFile, StringComparison.OrdinalIgnoreCase))
                return new(EditorPathAction.Rename, path);

            if (fileExists(path)) return new(EditorPathAction.Open, path);
            if (folderExists(path)) return new(EditorPathAction.Exists, path);

            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !folderExists(dir!)) return new(EditorPathAction.NotFound, path);
            return new(EditorPathAction.Rename, path);
        }
    }
}
