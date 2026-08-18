using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using KillerShell.Shell;
// App inherits Application.MainWindow (a Window property), which shadows the bare type name
// KillerShell.Shell.MainWindow for member access (though not for "new MainWindow()", which
// resolves to the type since a property can't be constructed). This alias sidesteps the clash.
using AppMainWindow = KillerShell.Shell.MainWindow;

namespace KillerShell
{
    public partial class App : Application
    {
        // ============================================================
        // Text selection rendering
        // ============================================================
        /// <summary>
        /// Turn OFF the adorner-based text selection renderer, app-wide and before any WPF text
        /// control exists.
        ///
        /// The adorner renderer paints the selection fill in an adorner layer ON TOP of the text
        /// and ignores SelectionTextBrush entirely. That is invisible while the fill is
        /// semi-transparent, which is what the twelve ordinary themes use - the glyphs read
        /// through it. 98SE's selection is a SOLID Win98 block (TextSelectionOpacity 1.0), so the
        /// fill covered the glyphs completely and a selected address bar came up as a plain navy
        /// rectangle with nothing legible in it (2026-08-08).
        ///
        /// The non-adorner renderer draws the fill BEHIND the run and honors SelectionTextBrush,
        /// so TextSelectionTextBrush (#ffffff on 98SE) actually reaches the glyphs.
        ///
        /// A STATIC ctor, not OnStartup: the switch is read once, the first time the framework
        /// touches the text stack, and OnStartup can already be too late. It is also global rather
        /// than per-theme - there is no way to flip it at runtime - but the other twelve themes
        /// pass SelectionTextBrush as TextBrush, so the non-adorner path renders them identically.
        ///
        /// Themes/98SE.xaml has documented this switch since the theme landed; the code
        /// to set it was never written, which is the whole bug.
        /// </summary>
        static App()
        {
            try
            {
                AppContext.SetSwitch(
                    "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering",
                    false);
            }
            catch { /* an older framework without the switch: the selection just stays as it was */ }
        }

        private const string RegKey = @"Software\KillerShell";

        // ============================================================
        // Paths (install system ported from KillerScan)
        // ============================================================

        private static readonly string AppName    = "KillerShell";
        private static readonly string ExeName    = "KillerShell.exe";
        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);
        private static readonly string InstallExe = Path.Combine(InstallDir, ExeName);

        // Machine-wide ("all users") install target. Used by the /silent path that winget, choco
        // and RMMs call, and by the Install for all users checkbox on the install prompt.
        private static readonly string MachineInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        private static readonly string MachineInstallExe = Path.Combine(MachineInstallDir, ExeName);

        private static readonly string StartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
        private static readonly string StartMenuLnk = Path.Combine(StartMenuDir, $"{AppName}.lnk");
        private static readonly string DesktopLnk   = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST       = 0x0000;

        // ============================================================
        // Startup
        // ============================================================

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Render on the CPU so the window isn't black over console-session screen-sharing tools
            // (ScreenConnect, Kaseya LiveConnect, VNC, TeamViewer). Negligible cost for this app.
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;

            // Silent install: KillerShell.exe /silent
            // Installs machine-wide to Program Files, no UI. Used by winget/choco/RMM.
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/silent", StringComparison.OrdinalIgnoreCase))
            {
                DoSilentInstall();
                Shutdown(0);
                return;
            }

            // Uninstall flag (called by Add/Remove Programs)
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Uninstall();
                Shutdown();
                return;
            }

            // Elevated retry of a recycle this user was refused: KillerShell.exe --recycle "<p>"...
            // Started by RecycleElevated (Elevation.cs) with the runas verb. It does the one job
            // and exits WITHOUT a window - the window that asked is still open behind it, and its
            // folder watcher picks the deletion up on its own.
            if (e.Args.Length > 1 &&
                string.Equals(e.Args[0], "--recycle", StringComparison.OrdinalIgnoreCase))
            {
                var targets = new System.Collections.Generic.List<string>();
                for (int i = 1; i < e.Args.Length; i++) targets.Add(e.Args[i]);

                // Silent, because there is no window here to pump the shell's progress dialog.
                // The exit code is this process's ONLY channel back to the window that started
                // it - Controlled Folder Access blocks by BINARY, not by token, so an elevated
                // retry can be refused exactly like the first attempt, and always exiting 0
                // made that look identical to a successful delete.
                var recycled = Services.FileOps.Recycle(targets, silent: true);
                Shutdown(recycled.Succeeded > 0 ? 0 : 1);
                return;
            }

            // Elevated retry of a document save Ctrl+F7's tab was refused, access-denied:
            // KillerShell.exe --elevated-save "<tempfile>" "<destpath>". Started by
            // RetrySaveElevated (Elevation.cs) with the runas verb. It does the one job and
            // exits WITHOUT a window - the window that asked is still open behind it and picks
            // the result up from this process's exit code, exactly like --recycle above.
            if (e.Args.Length > 2 &&
                string.Equals(e.Args[0], "--elevated-save", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(e.Args[1], e.Args[2], overwrite: true);
                    Shutdown(0);
                }
                catch
                {
                    Shutdown(1);
                }
                return;
            }

            // Demo / screenshot mode: KillerShell.exe --demo fills tabs with fabricated
            // results so marketing screenshots never leak real file names. It also shows
            // the About card in its signed state (About.cs) so captures taken from an
            // unsigned local build match the released one.
            AppMainWindow.DemoMode = Array.Exists(e.Args, a =>
                string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/demo",  StringComparison.OrdinalIgnoreCase));

            // A bare path on the command line. Explorer's "Open in KillerShell" verbs and the
            // file associations (Associations.cs) both launch us as `KillerShell.exe "<path>"`,
            // and without this the app would come up on Home and quietly ignore what it was
            // asked to open. Collected here, consumed once the window exists - a folder needs
            // the tree built before it can be navigated, and a file needs an editor tab.
            //
            // --shell and --cwd each take a VALUE, so their value is skipped rather than
            // treated as a path; otherwise an elevated relaunch would open its own working
            // directory a second time as a browse tab.
            for (int i = 0; i < e.Args.Length; i++)
            {
                string a = e.Args[i];
                if (a.Length == 0) continue;
                if (a[0] == '-' || a[0] == '/')
                {
                    if (string.Equals(a, "--shell", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "--cwd",   StringComparison.OrdinalIgnoreCase)) i++;
                    continue;
                }
                AppMainWindow.StartupPaths.Add(a);
            }

            // Persist the theme/accent choice in HKCU so it survives restarts.
            Services.ThemeManager.GetSetting = GetSetting;
            Services.ThemeManager.SetSetting = SetSetting;

            // Same persistence for the chosen UI language.
            Services.LocaleManager.GetSetting = GetSetting;
            Services.LocaleManager.SetSetting = SetSetting;

            // An elevated window themes itself separately, and defaults to Blood - see
            // ThemeManager.ThemeKey. Has to be set before Initialize, which is what reads it.
            Services.ThemeManager.Elevated = AppMainWindow.IsElevated;

            Services.ThemeManager.Initialize();    // restore saved theme before the window is built
            Services.LocaleManager.Initialize();   // then the saved language (en-US base + override)

            // Keep text-file.ico in sync with whatever THIS build embeds. EnsureFileIcon
            // otherwise only ever runs from RegisterAssociations (Associations.cs), so anyone who
            // registered under an older build and never opens the Associations card again would
            // keep serving a stale, rebranded-away icon for every text file defaulted to
            // KillerShell forever (2026-08-03 - exactly this bug). Cheap: EnsureFileIcon
            // already no-ops once the on-disk file matches the embedded one, so this is one file
            // read most launches. Gated on AssociationsRegistered so a portable copy nobody has
            // opted into associations for still gets no write here - "never registers anything on
            // its own" (Associations.cs file header) covers refreshing the icon too, not just
            // creating the association in the first place.
            if (AssociationsRegistered(machine: false) || AssociationsRegistered(machine: true))
                EnsureFileIcon();

            new MainWindow().Show();
        }

        // ============================================================
        // Preference store  (HKCU\Software\KillerShell)
        // ============================================================

        internal static string? GetSetting(string name)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RegKey);
                return k?.GetValue(name) as string;
            }
            catch { return null; }
        }

        internal static void SetSetting(string name, string value)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RegKey);
                k?.SetValue(name, value);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Values under RegKey that are NOT preferences. Written by the installer, read to decide
        /// whether this copy is portable, and so never cleared with the settings.
        /// </summary>
        private static readonly string[] InstallMarkers = { "Installed", "InstallPath", "Version" };

        /// <summary>
        /// Clear all Data: every saved preference, plus the temp files KillerShell extracted while
        /// browsing archives. The user's own files are never touched, and neither is the install.
        /// </summary>
        /// <remarks>
        /// Deletes VALUES one at a time rather than the key. KillerPDF's equivalent can afford a
        /// single DeleteSubKeyTree because its preferences live in their own Settings subkey; this
        /// app writes them straight into the app key, next to the install markers, so deleting the
        /// tree would take Installed/InstallPath/Version with it and the installed copy would come
        /// back up believing it is portable - Install badge in the footer, offering to install
        /// itself over itself.
        /// </remarks>
        internal static void ClearAllData()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
                if (k != null)
                {
                    foreach (var name in k.GetValueNames())
                    {
                        if (Array.IndexOf(InstallMarkers, name) >= 0) continue;
                        try { k.DeleteValue(name, throwOnMissingValue: false); } catch { }
                    }
                    // Nothing writes subkeys under here today, but a future feature might, and a
                    // preference hiding in one would survive a clear that only walked values.
                    foreach (var sub in k.GetSubKeyNames())
                        try { k.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); } catch { }
                }
            }
            catch { }

            // Archive entries extracted for opening or dragging out (ArchiveProvider.cs).
            TryDeleteDir(Path.Combine(Path.GetTempPath(), AppName));
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A whole-folder delete fails if any one file is locked - an archive entry still
                // open in another app is the normal case here. Remove what can be removed so the
                // rest clears now rather than nothing clearing at all.
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        try { File.Delete(f); } catch { }
                }
                catch { }
            }
        }

        // ============================================================
        // Portable badge / install (public surface used by MainWindow)
        // ============================================================

        /// <summary>True when running from outside the installed location (i.e. portable mode).
        /// Must check the machine-wide path as well as the per-user one: a /silent install from
        /// winget, choco or an RMM lands in Program Files, and comparing only against the
        /// per-user path would report those copies as portable.</summary>
        internal static bool IsPortable()
        {
            string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
            return !string.Equals(currentExe, InstallExe, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentExe, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when KillerShell is already installed machine-wide.</summary>
        internal static bool MachineInstallExists() => File.Exists(MachineInstallExe);

        /// <summary>Installs KillerShell, then relaunches from the installed location.
        /// For an all-users install the app re-runs itself elevated with /silent - the same
        /// machine-wide path winget and choco already use - so UAC only appears when the user
        /// actually asked for it. Returns false if that elevation was declined or failed.</summary>
        internal static bool InstallAndRelaunch(bool wantDesktop, bool allUsers)
        {
            if (allUsers)
            {
                if (!RunElevatedSilentInstall()) return false;

                // Only ever one install: drop the per-user copy so there is a single Start Menu
                // entry and a single uninstall entry. Settings are deliberately left alone.
                RemovePerUserInstall();

                Process.Start(new ProcessStartInfo(MachineInstallExe));
                Application.Current.Shutdown();
                return true;
            }

            DoInstall(wantDesktop);
            Process.Start(new ProcessStartInfo(InstallExe));
            Application.Current.Shutdown();
            return true;
        }

        /// <summary>Re-run this exe elevated with /silent and wait for it to finish.</summary>
        private static bool RunElevatedSilentInstall()
        {
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule!.FileName, "/silent")
                {
                    UseShellExecute = true,
                    Verb = "runas",          // triggers the UAC prompt
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p is not null && p.ExitCode == 0 && File.Exists(MachineInstallExe);
            }
            catch
            {
                // Declining the UAC prompt throws Win32Exception 1223 (ERROR_CANCELLED).
                return false;
            }
        }

        /// <summary>Remove a per-user install: files, shortcuts, and its HKCU install markers.
        /// Settings under the app's own registry key are deliberately left alone so theme,
        /// accent, locale and window placement survive the move to a machine-wide install.</summary>
        private static void RemovePerUserInstall()
        {
            try { if (File.Exists(StartMenuLnk)) File.Delete(StartMenuLnk); } catch { }
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch { }
            try { if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk); } catch { }
            try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true); } catch { }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
                key?.DeleteValue("Installed", throwOnMissingValue: false);
                key?.DeleteValue("InstallPath", throwOnMissingValue: false);
            }
            catch { }
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerShell", throwOnMissingSubKey: false);
            }
            catch { }

            // The per-user copy is going away, so its associations have to go with it. A
            // ProgID left pointing at a deleted exe is a dead row in Open With that nothing
            // in the UI would ever offer to clean up. (Associations.cs)
            UnregisterAssociations(machine: false);
        }

        // ============================================================
        // Silent (machine-wide) install -- used by winget / choco / RMM
        // ============================================================

        private static void DoSilentInstall()
        {
            try
            {
                string installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
                string installExe = Path.Combine(installDir, ExeName);
                string startMenuDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
                string startMenuLnk = Path.Combine(startMenuDir, $"{AppName}.lnk");

                Directory.CreateDirectory(installDir);
                string src = Process.GetCurrentProcess().MainModule!.FileName;
                File.Copy(src, installExe, overwrite: true);

                Directory.CreateDirectory(startMenuDir);
                CreateShortcut(startMenuLnk, installExe);

                string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

                using (var key = Registry.LocalMachine.CreateSubKey(@"Software\KillerShell"))
                {
                    key.SetValue("Installed",   1);
                    key.SetValue("InstallPath", installExe);
                    key.SetValue("Version",     version);
                }

                using (var key = Registry.LocalMachine.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerShell"))
                {
                    key.SetValue("DisplayName",          AppName);
                    key.SetValue("DisplayVersion",       version);
                    key.SetValue("Publisher",            "Steve / thekiller.net");
                    key.SetValue("InstallLocation",      installDir);
                    key.SetValue("DisplayIcon",          $"{installExe},0");
                    key.SetValue("UninstallString",      $"\"{installExe}\" /uninstall");
                    key.SetValue("QuietUninstallString", $"\"{installExe}\" /uninstall");
                    key.SetValue("NoModify",             1);
                    key.SetValue("NoRepair",             1);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Silent install failed: {ex.Message}");
                Environment.Exit(1);
            }
        }

        // ============================================================
        // Per-user install (the PORTABLE badge's Install button)
        // ============================================================

        private static void DoInstall(bool wantDesktop)
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                string src = Process.GetCurrentProcess().MainModule!.FileName;
                File.Copy(src, InstallExe, overwrite: true);

                Directory.CreateDirectory(StartMenuDir);
                CreateShortcut(StartMenuLnk, InstallExe);
                if (wantDesktop)
                    CreateShortcut(DesktopLnk, InstallExe);

                string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key.SetValue("Installed",   1);
                    key.SetValue("InstallPath", InstallExe);
                    key.SetValue("Version",     version);
                }

                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerShell"))
                {
                    key.SetValue("DisplayName",          AppName);
                    key.SetValue("DisplayVersion",       version);
                    key.SetValue("Publisher",            "Steve / thekiller.net");
                    key.SetValue("InstallLocation",      InstallDir);
                    key.SetValue("DisplayIcon",          $"{InstallExe},0");
                    key.SetValue("UninstallString",      $"\"{InstallExe}\" /uninstall");
                    key.SetValue("QuietUninstallString", $"\"{InstallExe}\" /uninstall");
                    key.SetValue("NoModify",             1);
                    key.SetValue("NoRepair",             1);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Installation failed:\n{ex.Message}", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void CreateShortcut(string lnkPath, string targetPath)
        {
            // Reflection over IDispatch instead of `dynamic` - avoids needing the
            // Microsoft.CSharp runtime binder reference this project doesn't carry.
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return;
                object shell = Activator.CreateInstance(shellType)!;
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, [lnkPath])!;
                var sc = shortcut.GetType();
                sc.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty,
                    null, shortcut, [targetPath]);
                sc.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty,
                    null, shortcut, [Path.GetDirectoryName(targetPath)!]);
                sc.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod,
                    null, shortcut, null);
            }
            catch { /* best-effort */ }
        }

        // ============================================================
        // Uninstall
        // ============================================================

        private static bool RelaunchMachineUninstallElevatedIfNeeded(bool machine)
        {
            if (!machine) return false;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                    return false;

                Process.Start(new ProcessStartInfo(
                    Process.GetCurrentProcess().MainModule!.FileName, "/uninstall")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC was declined. Leave the installation untouched.
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Uninstall could not request administrator access:\n{ex.Message}",
                    AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return true;
        }

        private static void Uninstall()
        {
            bool machine = string.Equals(Process.GetCurrentProcess().MainModule?.FileName,
                                         MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            if (RelaunchMachineUninstallElevatedIfNeeded(machine)) return;

            var res = MessageBox.Show(
                "Uninstall KillerShell from this computer?",
                $"{AppName} Uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            string startMenuDir = machine
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName)
                : StartMenuDir;
            string targetDir = machine ? MachineInstallDir : InstallDir;
            try { File.Delete(Path.Combine(startMenuDir, $"{AppName}.lnk")); } catch { }
            try { Directory.Delete(startMenuDir, recursive: false); } catch { }
            if (!machine) try { File.Delete(DesktopLnk); } catch { }

            // Associations first, while the keys they hang off still exist. Both scopes are
            // attempted: the HKLM half is a no-op unless this uninstall was launched elevated
            // from Add/Remove Programs, which is the only way a machine-wide install goes.
            // (Associations.cs)
            UnregisterAssociations(machine: false);
            UnregisterAssociations(machine: true);

            var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
            try { hive.DeleteSubKeyTree(RegKey, throwOnMissingSubKey: false); } catch { }
            try { hive.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerShell",
                throwOnMissingSubKey: false); } catch { }

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            // Self-delete: deferred via cmd batch so the EXE can exit first
            string bat = Path.Combine(Path.GetTempPath(), "killershell_uninstall.bat");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "ping -n 3 127.0.0.1 >nul\r\n" +
                $"rmdir /s /q \"{targetDir}\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                WindowStyle     = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            });

            MessageBox.Show("KillerShell has been uninstalled.", AppName,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
