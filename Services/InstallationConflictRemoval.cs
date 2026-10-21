using System;
using System.IO;

namespace KillerShell.Services
{
    internal static class InstallationConflictRemoval
    {
        internal static void Remove(bool machine, Action removeRegistration, Action<string?> restoreRegistration,
            UninstallRemoval.EnvironmentBoundary? environment = null)
        {
            environment ??= new UninstallRemoval.EnvironmentBoundary();
            using var removal = UninstallRemoval.Begin(machine, environment);
            try
            {
                string menu = Path.Combine(environment.Folder(machine ? Environment.SpecialFolder.CommonPrograms
                    : Environment.SpecialFolder.Programs), "KillerShell");
                DeleteShortcut(Path.Combine(menu, "KillerShell.lnk"));
                if (Directory.Exists(menu) && Directory.GetFileSystemEntries(menu).Length == 0)
                    Directory.Delete(menu, recursive: false);
                if (!machine)
                    DeleteShortcut(Path.Combine(environment.Folder(Environment.SpecialFolder.DesktopDirectory), "KillerShell.lnk"));
                removeRegistration();
                removal.Commit();
            }
            catch (Exception failure)
            {
                var errors = new System.Collections.Generic.List<Exception> { failure };
                try { removal.Rollback(failure); } catch (Exception error) { errors.Add(error); }
                try { restoreRegistration(removal.RecoveryImagePath); } catch (Exception error) { errors.Add(error); }
                if (errors.Count > 1)
                    throw new AggregateException("Installation conflict removal failed; recovery details follow.", errors);
                throw;
            }
        }

        private static void DeleteShortcut(string path)
        {
            for (string? item = path; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            {
                try
                {
                    if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                        throw new UnauthorizedAccessException("Installation conflict removal cannot traverse a linked shortcut path.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
