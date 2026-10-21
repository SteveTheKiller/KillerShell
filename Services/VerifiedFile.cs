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
    /// <summary>An update image is not signed by the publisher of the copy it would replace.</summary>
    internal sealed class PublisherMismatchException : Exception
    {
        internal PublisherMismatchException()
            : base("The update is not signed by the publisher of the installed copy. Nothing was changed.") { }
    }

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
            => TryCopy(stagedPath, expectedSha256, target, out _);

        internal static bool TryCopy(string stagedPath, string expectedSha256, string target, out string error)
            => TryCopy(stagedPath, expectedSha256, target, null, out error, out _);

        /// <summary>Exit code of the elevated save helper when the target changed on disk.</summary>
        internal const int ChangedOnDiskExit = 2;

        /// <param name="expectedDiskSha256">What the target must still hash to, or null for no check
        /// (<see cref="SafeFileWrite.Write(string, byte[], string?)"/>).</param>
        internal static bool TryCopy(string stagedPath, string expectedSha256, string target,
                                     string? expectedDiskSha256, out string error, out bool changedOnDisk)
        {
            error = string.Empty;
            changedOnDisk = false;
            byte[]? data = ReadVerified(stagedPath, expectedSha256);
            if (data == null) return false;
            try
            {
                SafeFileWrite.Write(target, data, expectedDiskSha256);
                return true;
            }
            catch (DiskChangedException ex) { changedOnDisk = true; error = ex.Message; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// Replaces an exe that may be running. A running image cannot be overwritten but can be
        /// renamed. Flush and verify a unique sibling before replacing with a backup. Failed
        /// publication retains recovery images and reports their paths.
        /// </summary>
        /// <returns>The exact retained backup created by this publication, or null for a new image.</returns>
        /// <param name="accept">
        /// Checked against the written replacement, beside the target and before anything is
        /// swapped. Refusal removes the replacement and throws <see cref="PublisherMismatchException"/>
        /// with the target unchanged.
        /// </param>
        internal static string? ReplaceImage(string target, byte[] data, Func<string, bool>? accept = null)
        {
            string fresh = target + ".new-" + Guid.NewGuid().ToString("N");
            string aside = target + OldMark + Guid.NewGuid().ToString("N")[..8];

            try
            {
                using (var stream = new FileStream(fresh, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                if (ReadVerified(fresh, Sha256(data)) == null)
                    throw new IOException("Replacement verification failed.");
                if (accept != null && !accept(fresh))
                {
                    try { File.Delete(fresh); } catch { }
                    throw new PublisherMismatchException();
                }
                if (File.Exists(target))
                {
                    File.Replace(fresh, target, aside);
                    return aside;
                }
                File.Move(fresh, target);
                return null;
            }
            catch (PublisherMismatchException) { throw; }
            catch (Exception ex)
            {
                Exception? rollback = null;
                if (!File.Exists(target) && File.Exists(aside))
                    try { File.Copy(aside, target); } catch (Exception restoreError) { rollback = restoreError; }
                throw new IOException($"Image publication failed. Target: {target}. Replacement: {fresh}. Backup: {aside}. "
                    + ex.Message + (rollback == null ? "" : " Recovery failed: " + rollback.Message), ex);
            }
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
