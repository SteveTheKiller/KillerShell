using System;
using System.IO;

namespace KillerShell.Services
{
    internal static class InstallationImage
    {
        /// <summary>
        /// Publishes <paramref name="source"/> over <paramref name="target"/> and retires the
        /// recovery images left behind. A failure to publish throws and leaves the previous image
        /// in place. A failure to retire them does NOT: by then the new image is installed, and
        /// the usual cause is the previous copy still running - a winget upgrade or an Install
        /// from a portable copy while the installed one is open - whose image Windows will not
        /// delete. Throwing there made the caller abandon the version keys, the Uninstall entry,
        /// PATH, shortcuts and associations with the new exe already on disk. The ownership
        /// receipt stays, so the next start (DeleteSetAside) or the next publish retires the
        /// backup once it is no longer running. Returns the cleanup failure text, or null.
        /// </summary>
        internal static string? Publish(string source, string target, Action<string>? deleteBackup = null)
        {
            string? backup = VerifiedFile.ReplaceImage(target, File.ReadAllBytes(source));
            try
            {
                if (backup != null && deleteBackup != null) deleteBackup(backup);
                RecoveryImages.Cleanup(target, includeUnpublished: true);
                return null;
            }
            catch (Exception error)
            {
                return "Installation image was published, but backup cleanup failed. Target: "
                    + target + ". Recovery image: " + backup + ". " + error.Message;
            }
        }
    }
}
