using System;
using System.IO;

namespace KillerShell.Services
{
    internal static class InstallationImage
    {
        internal static void Publish(string source, string target, Action<string>? deleteBackup = null)
        {
            string? backup = VerifiedFile.ReplaceImage(target, File.ReadAllBytes(source));
            if (backup == null) return;
            try { (deleteBackup ?? File.Delete)(backup); }
            catch (Exception error)
            {
                throw new IOException("Installation image was published, but backup cleanup failed. Target: "
                    + target + ". Recovery image: " + backup + ". " + error.Message, error);
            }
        }
    }
}
