using System;
using System.IO;
using System.Threading.Tasks;

namespace KillerShell.Shell
{
    /// <summary>
    /// Copy SHA-256: hash a file and put the hash on the clipboard. Every status goes to the
    /// sink captured when the command started, so a tab switch while a big file hashes does not
    /// send "copied" to whichever tab happens to be in front when it finishes.
    /// </summary>
    internal static class HashCopy
    {
        internal static async Task Run(string path, Func<string, Task<string?>> hash,
            Action<string, object[]> status, Func<string, bool> putOnClipboard)
        {
            string name = Path.GetFileName(path);
            status("Str_Status_Hashing", [name]);
            string? value = await hash(path);
            if (value == null) { status("Str_Status_HashFailed", []); return; }
            if (putOnClipboard(value)) status("Str_Status_HashCopied", [name]);
            else status("Str_Status_ClipboardBusy", []);
        }
    }
}
