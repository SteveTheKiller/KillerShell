using System;
using System.IO;
using System.Security.Cryptography;

namespace KillerShell.Services
{
    /// <summary>
    /// Writes for the elevated helpers. A file staged in the user's temp folder can be swapped
    /// by anything running as that user between the unelevated check and the elevated write, so
    /// the elevated side reads the staged file ONCE into memory, checks that copy against the
    /// hash it was launched with, and writes exactly those bytes. A swap after launch fails the
    /// check; a swap after the check changes nothing, because the file is never read again.
    /// </summary>
    internal static class VerifiedFile
    {
        /// <summary>Marker on the previous image, set aside while it may still be running.</summary>
        internal const string OldMark = ".old-";

        internal static string Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        /// <summary>The staged bytes, or null when they do not match <paramref name="expectedSha256"/>.</summary>
        internal static byte[]? ReadVerified(string stagedPath, string expectedSha256)
        {
            try
            {
                byte[] data = File.ReadAllBytes(stagedPath);
                return string.Equals(Sha256(data), expectedSha256, StringComparison.OrdinalIgnoreCase) ? data : null;
            }
            catch { return null; }
        }

        /// <summary>Copies a staged file over <paramref name="target"/> only if it still hashes to
        /// <paramref name="expectedSha256"/>. The target is untouched on any refusal or failed
        /// write (<see cref="SafeFileWrite"/>).</summary>
        internal static bool TryCopy(string stagedPath, string expectedSha256, string target)
        {
            byte[]? data = ReadVerified(stagedPath, expectedSha256);
            if (data == null) return false;
            try
            {
                SafeFileWrite.Write(target, data);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Replaces an exe that may be running. A running image cannot be overwritten but can be
        /// renamed, so the new bytes go to a sibling file, the current image is renamed aside and
        /// the new file takes its name. The old image is deleted when possible and otherwise
        /// left for <see cref="DeleteSetAside"/> at the next start. Throws on failure, with the
        /// original restored when it had already been moved.
        /// </summary>
        internal static void ReplaceImage(string target, byte[] data)
        {
            string fresh = target + ".new";
            string aside = target + OldMark + Guid.NewGuid().ToString("N")[..8];

            File.WriteAllBytes(fresh, data);
            bool movedAside = false;
            try
            {
                if (File.Exists(target)) { File.Move(target, aside); movedAside = true; }
                File.Move(fresh, target);
            }
            catch
            {
                if (movedAside && !File.Exists(target)) { try { File.Move(aside, target); } catch { } }
                try { File.Delete(fresh); } catch { }
                throw;
            }

            if (movedAside) { try { File.Delete(aside); } catch { /* still running; next start */ } }
        }

        /// <summary>Removes images set aside by an earlier <see cref="ReplaceImage"/>.</summary>
        internal static void DeleteSetAside(string exePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(exePath) ?? string.Empty;
                foreach (string f in Directory.GetFiles(dir, Path.GetFileName(exePath) + OldMark + "*"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
