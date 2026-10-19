using System;
using System.ComponentModel;
using System.Diagnostics;

namespace KillerShell.Services
{
    internal enum ShellOpenResult { Opened, Declined, Failed }

    /// <summary>
    /// Opens a path through its Windows association. ShellExecute throws for ordinary answers:
    /// No at a UAC prompt for an installer, a policy or SmartScreen block, a file deleted since
    /// it was listed. Inside an event handler any of those used to close the whole window, so
    /// every open from the file list goes through here and gets a result to report instead.
    /// </summary>
    internal static class ShellOpen
    {
        private const int ErrorCancelled = 1223;

        internal static ShellOpenResult Open(string path, string? verb = null)
        {
            try
            {
                var psi = new ProcessStartInfo(path) { UseShellExecute = true };
                if (verb != null) psi.Verb = verb;
                using (Process.Start(psi)) { }
                return ShellOpenResult.Opened;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return ShellOpenResult.Declined;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                             or System.IO.FileNotFoundException)
            {
                return ShellOpenResult.Failed;
            }
        }
    }
}
