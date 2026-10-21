using System.IO;

namespace KillerShell.Services
{
    internal static class InstallationImage
    {
        internal static void Publish(string source, string target)
            => VerifiedFile.ReplaceImage(target, File.ReadAllBytes(source));
    }
}
