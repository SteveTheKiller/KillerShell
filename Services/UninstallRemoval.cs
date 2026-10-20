using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace KillerShell.Services
{
    /// <summary>Removes only the fixed installation image, generated icon and empty directory.</summary>
    internal static class UninstallRemoval
    {
        internal static void Remove(bool machine)
        {
            string parent = machine
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            string directory = Path.Combine(parent, "KillerShell");
            string image = Path.Combine(directory, "KillerShell.exe");
            string icon = Path.Combine(directory, "text-file.ico");
            string current = Process.GetCurrentProcess().MainModule!.FileName;
            string? detached = null;
            RejectLinks(directory);
            if (!Directory.Exists(directory)) return;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                if (!string.Equals(entry, image, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry, icon, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The installation contains unrelated files. They were preserved.");
            foreach (var process in Process.GetProcessesByName("KillerShell"))
                using (process)
                {
                    if (process.Id == Process.GetCurrentProcess().Id) continue;
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase))
                            throw new System.ComponentModel.Win32Exception(KillerFamily.Deployment.InstallerExitCodes.Busy);
                    }
                    catch (InvalidOperationException) { /* Exited before inspection. */ }
                }
            try
            {
                RejectLinks(icon);
                if (File.Exists(image))
                {
                    RejectLinks(image);
                    if (string.Equals(current, image, StringComparison.OrdinalIgnoreCase))
                    {
                        // Same-volume rename keeps the live image in trusted storage while
                        // allowing removal of the installation to complete before returning.
                        detached = Path.Combine(parent, ".KillerShell-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                        File.Move(image, detached);
                    }
                    else File.Delete(image);
                }
                File.Delete(icon);
                Directory.Delete(directory, recursive: false);
                if (Directory.Exists(directory) || File.Exists(image))
                    throw new IOException("The installation could not be removed.");
                if (detached != null) StartImageCleanup(detached);
            }
            catch
            {
                // A failed helper launch or removal leaves a runnable image and installer
                // metadata available for retry. Never overwrite a replacement installation.
                if (detached != null && File.Exists(detached) && !File.Exists(image))
                {
                    Directory.CreateDirectory(directory);
                    File.Move(detached, image);
                }
                throw;
            }
        }

        private static void RejectLinks(string path)
        {
            for (string? item = path; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
                if ((Directory.Exists(item) || File.Exists(item)) &&
                    (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Uninstall cannot traverse a linked installation path.");
        }

        private static void StartImageCleanup(string image)
        {
            using var child = Process.Start(CleanupStartInfo(image));
            if (child == null) throw new IOException("The uninstall image cleanup helper did not start.");
        }

        internal static ProcessStartInfo CleanupStartInfo(string image)
        {
            string data = Convert.ToBase64String(Encoding.Unicode.GetBytes(image));
            // The executable is the fixed Windows system helper. All code is immutable in
            // its command line; the path is decoded as data, never interpreted as commands.
            string script = "$ErrorActionPreference='Stop'; try { "
                + "$path=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + data + "')); "
                + "for($attempt=0;$attempt -lt 300;$attempt++){try{[IO.File]::Delete($path);exit 0}catch [IO.IOException]{[Threading.Thread]::Sleep(100)}catch [UnauthorizedAccessException]{[Threading.Thread]::Sleep(100)}};exit 1 "
                + "} catch { exit 1 }";
            string helper = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            return new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
                    + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            };
        }
    }
}
