using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace KillerShell.Services
{
    /// <summary>Removes only the fixed installation image, generated icon and empty directory.</summary>
    internal static class UninstallRemoval
    {
        internal sealed class EnvironmentBoundary
        {
            internal Func<Environment.SpecialFolder, string> Folder = Environment.GetFolderPath;
            internal Action<string> Cleanup = StartImageCleanup;
            internal Func<Process[]> Processes = () => Process.GetProcessesByName("KillerShell");
        }

        internal static void Remove(bool machine)
        {
            using var removal = Begin(machine);
            removal.Commit();
        }

        internal static Transaction Begin(bool machine, Process? authenticatedCoordinator = null)
            => Begin(machine, new EnvironmentBoundary(), authenticatedCoordinator);

        internal static Transaction Begin(bool machine, EnvironmentBoundary environment, Process? authenticatedCoordinator = null)
        {
            string parent = machine
                ? environment.Folder(Environment.SpecialFolder.ProgramFiles)
                : Path.Combine(environment.Folder(Environment.SpecialFolder.LocalApplicationData), "Programs");
            string directory = Path.Combine(parent, "KillerShell");
            string image = Path.Combine(directory, "KillerShell.exe");
            string icon = Path.Combine(directory, "text-file.ico");
            RejectLinks(directory);
            if (machine) ValidateMachineSecurity(directory, parent);
            var transaction = new Transaction(directory, image, icon, parent, environment.Cleanup);
            if (!Directory.Exists(directory)) return transaction;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                if (!string.Equals(entry, image, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry, icon, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The installation contains unrelated files. They were preserved.");
            foreach (var process in environment.Processes())
                using (process)
                {
                    if (process.Id == Process.GetCurrentProcess().Id) continue;
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase)
                            && !IsAuthenticatedCoordinator(process, authenticatedCoordinator, image))
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
                    transaction.StageImage();
                }
                if (File.Exists(icon)) transaction.StageIcon();
                Directory.Delete(directory, recursive: false);
                if (Directory.Exists(directory) || File.Exists(image))
                    throw new IOException("The installation could not be removed.");
                return transaction;
            }
            catch (Exception error)
            {
                transaction.Rollback(error);
                throw;
            }
        }

        private static bool IsAuthenticatedCoordinator(Process candidate, Process? coordinator, string image)
        {
            if (coordinator == null || candidate.Id != coordinator.Id) return false;
            _ = coordinator.Handle;
            return !coordinator.HasExited && !candidate.HasExited
                && candidate.StartTime.ToUniversalTime().Ticks == coordinator.StartTime.ToUniversalTime().Ticks
                && string.Equals(coordinator.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase);
        }

        internal static void ValidateMachineImage(string expectedImage)
        {
            string parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string directory = Path.Combine(parent, "KillerShell");
            if (!string.Equals(Path.GetFullPath(expectedImage), Path.Combine(directory, "KillerShell.exe"), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The uninstall image is outside the fixed machine installation.");
            RejectLinks(expectedImage);
            ValidateMachineSecurity(directory, parent);
        }

        private static void ValidateMachineSecurity(string directory, string parent)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The uninstall directory is outside its trusted parent.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier? installer = null;
            try { installer = (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier)); }
            catch (IdentityNotMappedException) { }
            bool Trusted(IdentityReference sid) => sid.Equals(system) || sid.Equals(administrators) || sid.Equals(installer);
            for (string? item = directory; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            {
                if (!Directory.Exists(item)) continue;
                var security = Directory.GetAccessControl(item, AccessControlSections.Owner | AccessControlSections.Access);
                if (!Trusted(security.GetOwner(typeof(SecurityIdentifier))))
                    throw new UnauthorizedAccessException("The machine uninstall path is not owned by a trusted administrator or system principal.");
                bool installation = string.Equals(item, directory, StringComparison.OrdinalIgnoreCase);
                FileSystemRights dangerous = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                    | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if (installation) dangerous |= FileSystemRights.Write;
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow
                        && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                        && (rule.FileSystemRights & dangerous) != 0 && !Trusted(rule.IdentityReference))
                        throw new UnauthorizedAccessException("The machine uninstall path permits changes by an untrusted principal.");
            }
            foreach (string file in new[] {Path.Combine(directory, "KillerShell.exe"), Path.Combine(directory, "text-file.ico")})
            {
                if (!File.Exists(file)) continue;
                var security = File.GetAccessControl(file, AccessControlSections.Owner | AccessControlSections.Access);
                if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("The uninstall file owner is not trusted.");
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow
                        && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0
                        && !Trusted(rule.IdentityReference)) throw new UnauthorizedAccessException("The uninstall file is writable by an untrusted principal.");
            }
        }

        internal sealed class Transaction : IDisposable
        {
            private readonly string _directory, _image, _icon, _parent;
            private readonly Action<string> _cleanup;
            private string? _detachedImage, _detachedIcon;
            private byte[]? _iconRecoveryBytes;
            private bool _finished;
            internal string? RecoveryImagePath => _detachedImage != null && File.Exists(_detachedImage) ? _detachedImage : null;
            internal Transaction(string directory, string image, string icon, string parent, Action<string> cleanup)
            { _directory=directory; _image=image; _icon=icon; _parent=parent; _cleanup=cleanup; }
            internal void StageImage()
            {
                string destination = Path.Combine(_parent, ".KillerShell-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Move(_image, destination);
                _detachedImage = destination;
            }
            internal void StageIcon()
            {
                string destination = Path.Combine(_parent, ".KillerShell-uninstall-" + Guid.NewGuid().ToString("N") + ".ico");
                File.Move(_icon, destination);
                _detachedIcon = destination;
            }
            internal void Commit()
            {
                if (_finished) throw new InvalidOperationException("The uninstall transaction has already finished.");
                try
                {
                    if (Directory.Exists(_directory) || File.Exists(_directory) || File.Exists(_image)) throw new IOException("A replacement installation appeared. It was preserved.");
                    if (_detachedIcon != null)
                    {
                        _iconRecoveryBytes = File.ReadAllBytes(_detachedIcon);
                        File.Delete(_detachedIcon);
                    }
                    if (_detachedImage != null) _cleanup(_detachedImage);
                    _finished = true;
                }
                catch (Exception error) { Rollback(error); throw; }
            }
            internal void Rollback(Exception? original = null)
            {
                if (_finished) return;
                _finished = true;
                Exception? recoveryError = null;
                try
                {
                    RejectLinks(_directory);
                    if (_detachedImage != null || _detachedIcon != null) Directory.CreateDirectory(_directory);
                    if (_detachedImage != null && File.Exists(_detachedImage)) File.Move(_detachedImage, _image);
                }
                catch (Exception error) { recoveryError = error; }
                try
                {
                    RejectLinks(_directory);
                    if (_detachedIcon != null && File.Exists(_detachedIcon)) File.Move(_detachedIcon, _icon);
                    else if (_iconRecoveryBytes != null)
                    {
                        using var restored = new FileStream(_icon, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        restored.Write(_iconRecoveryBytes, 0, _iconRecoveryBytes.Length);
                        restored.Flush(true);
                    }
                }
                catch (Exception error) { recoveryError = recoveryError == null ? error : new AggregateException(recoveryError, error); }
                if (recoveryError != null)
                    throw new IOException("Uninstall recovery is incomplete. Image recovery location: "
                        + (RecoveryImagePath ?? (File.Exists(_image) ? _image : "none"))
                        + ". Icon recovery location: " + (_detachedIcon != null && File.Exists(_detachedIcon) ? _detachedIcon : File.Exists(_icon) ? _icon : "none") + ". " + original?.Message,
                        original == null ? recoveryError : new AggregateException(original, recoveryError));
            }
            public void Dispose() => Rollback();
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
