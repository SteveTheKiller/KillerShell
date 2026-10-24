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

        /// <summary>Tells Explorer to drop what it remembers of verbs and associations.</summary>
        internal static void NotifyAssociationsChanged()
        {
            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        // ============================================================
        // Startup
        // ============================================================

        protected override void OnStartup(StartupEventArgs e)
        {
            HookCrashLogging();   // CrashLog.cs - first, so it covers startup itself
            KillerFamily.Accessibility.AccessibleControls.Initialize();
            KillerFamily.Theming.AccentShadow.Initialize();
            var cliArgs = KillerFamily.CommandLine.CommandLineEntry.GetArguments(e.Args,
                ["search", "list", "info", "read", "processes", "services", "events", "registry", "drives", "hash", "version", "--version"]);
            if (cliArgs != null)
            {
                Services.CliConsole.AttachToParent();
                using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                using var error = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(output);
                Console.SetError(error);
                if (cliArgs.Length > 0 && cliArgs[0] == "version") cliArgs[0] = "--version";
                Shutdown(KillerShell.Cli.Program.Main(cliArgs));
                return;
            }

            base.OnStartup(e);
            // Every window (dialogs, pickers) takes the family UI face from the active theme unless it
            // sets its own, so no dialog falls back to the system Segoe UI.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
            {
                if (s is Window w && DependencyPropertyHelper.GetValueSource(w, System.Windows.Controls.Control.FontFamilyProperty).BaseValueSource == BaseValueSource.Default)
                    w.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "UiFont");
            }));
            ContextMenuFlip.Register();   // Controls/ContextMenuFlip.cs - menus that open upward
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppMainWindow.PublishGrainTile();   // install and uninstall dialogs open before any main window

            // Render on the CPU so the window isn't black over console-session screen-sharing tools
            // (ScreenConnect, Kaseya LiveConnect, VNC, TeamViewer). Negligible cost for this app.
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;

            // Silent install: KillerShell.exe /silent
            // No UI. With admin rights (winget/choco/RMM) it installs machine-wide to Program
            // Files; without them (the Microsoft Store) it installs for the current user.
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/silent", StringComparison.OrdinalIgnoreCase))
            {
                Shutdown(DoSilentInstall());
                return;
            }

            // Standalone installer dialogs need the saved language before their early return.
            Services.LocaleManager.GetSetting = GetSetting;
            Services.LocaleManager.SetSetting = SetSetting;
            Services.LocaleManager.Initialize();

            // Uninstall flags (Add/Remove Programs runs /uninstall; package managers run
            // /uninstall-silent, the registered QuietUninstallString, which shows no UI)
            if (e.Args.Length > 0 &&
                (string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e.Args[0], "/uninstall-silent", StringComparison.OrdinalIgnoreCase)))
            {
                TaskbarIdentity.UseUninstall();
                Shutdown(Uninstall(string.Equals(e.Args[0], "/uninstall-silent", StringComparison.OrdinalIgnoreCase), e.Args));
                return;
            }

            // Elevated half of the dual-install repair: removes the machine-wide copy.
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/remove-machine-conflict", StringComparison.OrdinalIgnoreCase))
            {
                Shutdown(RemoveMachineInstallConflict());
                return;
            }

            if (e.Args.Length > 0 && e.Args[0] == Services.ProcessRestart.ElevatedArgument)
            {
                Shutdown(Services.ProcessRestart.RunElevatedHelper(e.Args));
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
            // KillerShell.exe --elevated-save "<tempfile>" "<destpath>" <sha256> [reply-pipe [disk-sha256|-]]. Started by
            // RetrySaveElevated (Elevation.cs) with the runas verb. It does the one job and
            // exits WITHOUT a window - the window that asked is still open behind it and picks
            // the result up from this process's exit code and optional private diagnostic pipe.
            // The staged file is in the user's temp folder, so only bytes that still match the
            // hash taken when it was staged are written (Services/VerifiedFile.cs).
            if (e.Args.Length > 3 &&
                string.Equals(e.Args[0], "--elevated-save", StringComparison.OrdinalIgnoreCase))
            {
                // The last argument is what the target held when the document last read or wrote
                // it, so a file another program changed while UAC was up is refused, not replaced.
                string? diskBaseline = e.Args.Length > 5 && e.Args[5] != "-" ? e.Args[5] : null;
                bool saved = Services.VerifiedFile.TryCopy(e.Args[1], e.Args[3], e.Args[2], diskBaseline,
                                                           out string error, out bool changedOnDisk);
                if (e.Args.Length > 4) Services.ElevatedSaveResponse.Send(e.Args[4], error);
                Shutdown(saved ? 0 : changedOnDisk ? Services.VerifiedFile.ChangedOnDiskExit : 1);
                return;
            }

            // Elevated half of an update to a machine-wide install:
            // KillerShell.exe --apply-update "<download>" <sha256> <pid> "<version>".
            // Started by UpdateService.StartSwap from the installed exe, so the code running
            // elevated is the copy only an administrator could have placed. No window.
            if (e.Args.Length > 4 &&
                string.Equals(e.Args[0], Services.UpdateService.ApplyUpdateArgument, StringComparison.OrdinalIgnoreCase))
            {
                Shutdown(ApplyElevatedUpdate(e.Args[1], e.Args[2], e.Args[3], e.Args[4]));
                return;
            }

            // An update leaves the replaced image set aside while it is still running.
            try { Services.VerifiedFile.DeleteSetAside(Process.GetCurrentProcess().MainModule!.FileName); } catch { }

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
                        string.Equals(a, "--terminal-profile", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "--cwd",   StringComparison.OrdinalIgnoreCase)) i++;
                    continue;
                }
                AppMainWindow.StartupPaths.Add(a);
            }

            // Persist the theme/accent choice in HKCU so it survives restarts.
            Services.ThemeManager.GetSetting = GetSetting;
            Services.ThemeManager.SetSetting = SetSetting;

            // An elevated window themes itself separately, and defaults to Blood - see
            // ThemeManager.ThemeKey. Has to be set before Initialize, which is what reads it.
            Services.ThemeManager.Elevated = AppMainWindow.IsElevated;

            Services.ThemeManager.Initialize();    // restore saved theme before the window is built
            KillerFamily.Layout.ShortcutHints.Initialize(GetSetting, SetSetting);
            Services.FilmGrain.Initialize();       // then the Film Grain choice, which outranks it

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
                EnsureFileIcon(ExePath);
            RefreshTerminalClass();   // DefaultTerminal.cs - installs from before the class existed

            // Started by COM for one console program, with KillerShell the default terminal
            // (DefaultTerminal.cs). That program's window is all this process is for.
            if (IsComServerStart(e.Args))
            {
                StartForConsoleHandoff();
                return;
            }

            OfferInstallConflictRepair();
            // Start with Windows (About card): keep the Run value pointing at this exe.
            KillerFamily.Deployment.StartupRegistration.Refresh(Services.AppInfo.DisplayName);

            ShutdownMode = ShutdownMode.OnLastWindowClose;
            MainWindow = new MainWindow();
            // Started from the Run value: open minimized on the taskbar. After SourceInitialized,
            // where the window takes its saved size and maximized state (WindowBounds.cs), so the
            // first restore comes back maximized when the last window was.
            if (KillerFamily.Deployment.StartupRegistration.HasMinimizedArgument(e.Args))
            {
                var window = MainWindow;
                window.SourceInitialized += (_, _) => KillerFamily.Deployment.StartupRegistration.StartMinimized(window);
            }
            MainWindow.Show();
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
        private static readonly string[] InstallMarkers = ["Installed", "InstallPath", "Version"];

        /// <summary>
        /// What Clear all Data leaves, by value or subkey name: the install markers, and what the
        /// About card's default terminal and folder settings replaced (DefaultTerminal.cs). Those
        /// are not preferences but what turning the settings off, or uninstalling, puts back.
        /// </summary>
        internal static bool SurvivesClearAllData(string valueName)
            => Array.IndexOf(InstallMarkers, valueName) >= 0 ||
               string.Equals(valueName, Services.DefaultTerminalSetting.PreviousSetting, StringComparison.OrdinalIgnoreCase) ||
               valueName.StartsWith(Services.FolderOpenerSetting.PreviousSetting, StringComparison.OrdinalIgnoreCase);

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
                        if (SurvivesClearAllData(name)) continue;
                        try { k.DeleteValue(name, throwOnMissingValue: false); } catch { }
                    }
                    // Nothing writes subkeys under here today, but a future feature might, and a
                    // preference hiding in one would survive a clear that only walked values.
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        if (SurvivesClearAllData(sub)) continue;
                        try { k.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); } catch { }
                    }
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

        /// <summary>True when KillerShell is already installed for the current user.</summary>
        internal static bool UserInstallExists() => File.Exists(InstallExe);

        /// <summary>Repairs a machine that carries BOTH a per-user and a machine-wide install -
        /// the state where each Add/Remove Programs entry describes the other copy's version and
        /// launching gets whichever exe the shell resolves first. Detected at startup; offers to
        /// remove whichever copy is NOT running. Removing the machine copy needs elevation, so
        /// that path re-runs this exe with /remove-machine-conflict under UAC.</summary>
        private static void OfferInstallConflictRepair()
        {
            if (!File.Exists(InstallExe) || !File.Exists(MachineInstallExe)) return;
            string current = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            bool runningMachine = string.Equals(current, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            bool runningUser = string.Equals(current, InstallExe, StringComparison.OrdinalIgnoreCase);
            if (!runningMachine && !runningUser) return;

            // Runs after LocaleManager.Initialize (OnStartup), so the saved language is live here.
            // Two whole sentences rather than one with the scope spliced in: "per-user" and
            // "all-users" do not drop into the same slot in every language.
            var confirm = new ConfirmDialog(
                AppMainWindow.LocStatic(runningMachine ? "Str_Dlg_InstalledTwiceUser" : "Str_Dlg_InstalledTwiceMachine"),
                null, AppMainWindow.LocStatic("Str_Btn_Yes"), installer: true);
            confirm.ShowDialog();
            if (!confirm.Confirmed) return;

            try
            {
                if (runningMachine) RemovePerUserInstall();
                else
                {
                    using var p = Process.Start(new ProcessStartInfo(current, "/remove-machine-conflict")
                    { UseShellExecute = true, Verb = "runas" });
                    if (p == null) throw new IOException("The installation repair helper could not start.");
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new System.ComponentModel.Win32Exception(p.ExitCode);
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            catch (Exception ex) { ShowInstallConflictFailure(ex); }
        }

        private static int RemoveMachineInstallConflict()
        {
            try
            {
                if (!KillerFamily.Deployment.InstallerExitCodes.IsElevated())
                    return KillerFamily.Deployment.InstallerExitCodes.AccessDenied;
                RemoveInstallConflict(machine: true);
                return KillerFamily.Deployment.InstallerExitCodes.Success;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return KillerFamily.Deployment.InstallerExitCodes.FromException(ex);
            }
        }

        private static void ShowInstallConflictFailure(Exception error)
        {
            var owner = Current.MainWindow is { IsVisible: true } window ? window : null;
            new ConfirmDialog(AppMainWindow.LocStatic("Str_Status_OpFailed") + Environment.NewLine + error.Message,
                null, AppMainWindow.LocStatic("Str_Btn_OK"), installer: owner == null, notification: true)
                { Owner = owner }.ShowDialog();
        }

        /// <summary>Installs KillerShell, then relaunches from the installed location.
        /// For an all-users install the app re-runs itself elevated with /silent - the same
        /// machine-wide path winget and choco already use - so UAC only appears when the user
        /// actually asked for it. Returns false if that elevation was declined or failed.</summary>
        internal static bool InstallAndRelaunch(bool wantDesktop, bool allUsers)
        {
            // UI normally locks the machine-wide choice, but enforce the one-install rule here
            // too so another caller can never create a per-user copy beside Program Files.
            if (!allUsers && MachineInstallExists())
                return false;

            if (allUsers)
            {
                if (!RunElevatedSilentInstall()) return false;

                // Only ever one install: drop the per-user copy so there is a single Start Menu
                // entry and a single uninstall entry. Settings are deliberately left alone.
                try { RemovePerUserInstall(); }
                catch (Exception ex) { ShowInstallConflictFailure(ex); return false; }
                SetDesktopShortcut(wantDesktop, MachineInstallExe);

                Process.Start(new ProcessStartInfo(MachineInstallExe));
                Application.Current.Shutdown();
                return true;
            }

            if (!DoInstall(wantDesktop)) return false;
            if (!File.Exists(InstallExe)) return false;
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
                if (p == null) throw new IOException("The installation helper could not start.");
                p.WaitForExit();
                if (p.ExitCode != 0) throw new System.ComponentModel.Win32Exception(p.ExitCode);
                return File.Exists(MachineInstallExe);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false;
            }
            catch (Exception ex) { ShowInstallConflictFailure(ex); return false; }
        }

        /// <summary>Remove a per-user install: files, shortcuts, and its HKCU install markers.
        /// Settings under the app's own registry key are deliberately left alone so theme,
        /// accent, locale and window placement survive the move to a machine-wide install.</summary>
        private static void RemovePerUserInstall()
            => RemoveInstallConflict(machine: false);

        private static void RemoveInstallConflict(bool machine)
        {
            using var lease = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppName);
            const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerShell";
            var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
            var metadata = new Services.UninstallMetadata(hive, RegKey, uninstallKey);
            Services.InstallationConflictRemoval.Remove(machine, () =>
            {
                Services.CommandLinePath.UnregisterRequired(machine ? MachineInstallDir : InstallDir, machine);
                // The other install stays and carries "Open folders in KillerShell" on.
                if (!UnregisterAssociations(machine, keepFolderOpener: true)) throw new UnauthorizedAccessException();
                // Anyone else who turned folder opening on from the all-users copy would be left
                // with an open verb naming the deleted exe; put back what they had, as an
                // uninstall does. The account asking for this is already on its own copy.
                if (machine)
                {
                    TurnOffFolderOpenerForOtherUsers();      // DefaultTerminal.cs
                    // And the administrator running this, when it is not the account that asked:
                    // only an open verb that still runs the all-users copy is touched.
                    TurnOffFolderOpenerFor(MachineInstallExe);
                }
                // A per-user class left behind would win for this user and point at a deleted exe.
                // When the all-users copy is the one staying, point this user's class at it: an
                // all-users copy from before the class existed has none of its own until it runs
                // elevated. The same goes for the open verb, which is only ever this user's and
                // names one exe: a winget or RMM install over a per-user copy may not start the
                // all-users one for a long time.
                RemoveTerminalClass(machine, restoreDefault: false);   // DefaultTerminal.cs
                if (!machine)
                {
                    RegisterTerminalClass(machine: false, MachineInstallExe);
                    RepointFolderOpener(MachineInstallExe);
                }
                using (var key = hive.OpenSubKey(RegKey, writable: true))
                    foreach (string marker in InstallMarkers) key?.DeleteValue(marker, throwOnMissingValue: false);
                hive.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false);
            }, recovery =>
            {
                metadata.Restore();
                if (recovery == null) return;
                using var install = hive.CreateSubKey(RegKey);
                install.SetValue("RecoveryImagePath", recovery);
                using var entry = hive.CreateSubKey(uninstallKey);
                entry.SetValue("RecoveryImagePath", recovery);
            });
        }

        // ============================================================
        // Silent install, used by winget / choco / RMM and the Microsoft Store
        // ============================================================

        private static int DoSilentInstall()
        {
            try
            {
                using var installLease = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppName);

                // An unsigned or tampered exe must not be able to write itself into
                // Program Files, least of all while elevated.
                var (valid, _, _) = Services.CodeSignature.GetSignerInfo();
                if (!valid)
                {
                    Console.Error.WriteLine("Silent install refused: EXE has no valid Authenticode signature.");
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                }

                // Without admin rights, as the Microsoft Store runs installers, this is the normal
                // per-user install with no prompt. The app never needs admin rights.
                if (!KillerFamily.Deployment.InstallerExitCodes.IsElevated())
                {
                    InstallForCurrentUser(wantDesktop: false);
                    return KillerFamily.Deployment.InstallerExitCodes.Success;
                }

                string installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
                string installExe = Path.Combine(installDir, ExeName);
                string startMenuDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
                string startMenuLnk = Path.Combine(startMenuDir, $"{AppName}.lnk");

                Directory.CreateDirectory(installDir);
                string src = Process.GetCurrentProcess().MainModule!.FileName;
                string? leftover = Services.InstallationImage.Publish(src, installExe);
                if (leftover != null) Console.Error.WriteLine(leftover);
                Services.CommandLinePath.Register(installDir, machine: true);

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
                    key.SetValue("Publisher",            "Steve the Killer");
                    key.SetValue("InstallLocation",      installDir);
                    key.SetValue("DisplayIcon",          $"{installExe},0");
                    key.SetValue("UninstallString",      $"\"{installExe}\" /uninstall");
                    key.SetValue("QuietUninstallString", $"\"{installExe}\" /uninstall-silent");
                    key.SetValue("NoModify",             1);
                    key.SetValue("NoRepair",             1);
                }

                RegisterAssociations(machine: true, exe: installExe);
                RegisterTerminalClass(machine: true, installExe);   // DefaultTerminal.cs

                // `/silent` is also the winget/choco/RMM entry point, so duplicate cleanup
                // belongs here rather than only in the interactive caller. Settings survive.
                // It runs LAST and cannot fail the install: a per-user copy that is open, or a
                // per-user folder holding files the installer does not own, made it throw after
                // the machine keys were written and before associations and the terminal class,
                // leaving a half-registered machine install and an error code for the package
                // manager. The machine copy's own first launch offers the same removal again
                // (OfferInstallConflictRepair).
                try { RemovePerUserInstall(); }
                catch (Exception conflict)
                {
                    Console.Error.WriteLine("Installed for all users. The per-user copy was left in place: " + conflict.Message);
                }
                return KillerFamily.Deployment.InstallerExitCodes.Success;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Silent install failed: {ex.Message}");
                return KillerFamily.Deployment.InstallerExitCodes.FromException(ex);
            }
        }

        // ============================================================
        // Per-user install (the PORTABLE badge's Install button)
        // ============================================================

        private static bool DoInstall(bool wantDesktop)
        {
            try
            {
                InstallForCurrentUser(wantDesktop);
                return true;
            }
            catch (Exception ex)
            {
                var owner = Current.MainWindow is { IsVisible: true } window ? window : null;
                new ConfirmDialog(string.Format(AppMainWindow.LocStatic("Str_Dlg_InstallFailed"), ex.Message),
                    null, AppMainWindow.LocStatic("Str_Btn_OK"),
                    installer: owner == null, notification: true) { Owner = owner }.ShowDialog();
                return false;
            }
        }

        /// <summary>The per-user install itself, shared by the Install button and a silent install
        /// without admin rights. Failures propagate to the caller.</summary>
        private static void InstallForCurrentUser(bool wantDesktop)
        {
            Directory.CreateDirectory(InstallDir);
            string src = Process.GetCurrentProcess().MainModule!.FileName;
            // A cleanup leftover (the previous copy still running) is retired on a later start.
            Services.InstallationImage.Publish(src, InstallExe);
            Services.CommandLinePath.Register(InstallDir, machine: false);

            Directory.CreateDirectory(StartMenuDir);
            CreateShortcut(StartMenuLnk, InstallExe);
            SetDesktopShortcut(wantDesktop, InstallExe);

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
                key.SetValue("Publisher",            "Steve the Killer");
                key.SetValue("InstallLocation",      InstallDir);
                key.SetValue("DisplayIcon",          $"{InstallExe},0");
                key.SetValue("UninstallString",      $"\"{InstallExe}\" /uninstall");
                key.SetValue("QuietUninstallString", $"\"{InstallExe}\" /uninstall-silent");
                key.SetValue("NoModify",             1);
                key.SetValue("NoRepair",             1);
            }
            RegisterAssociations(machine: false, exe: InstallExe);
            RegisterTerminalClass(machine: false, InstallExe);   // DefaultTerminal.cs
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

        private static void SetDesktopShortcut(bool enabled, string targetPath)
        {
            if (enabled)
                CreateShortcut(DesktopLnk, targetPath);
            else
                try { if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk); } catch { }
        }

        // ============================================================
        // Uninstall
        // ============================================================

        private static int? RelaunchMachineUninstallElevatedIfNeeded(bool machine, bool silent)
        {
            if (!machine) return null;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                    return null;

                Services.UninstallRemoval.ValidateMachineImage(MachineInstallExe);
                using var relay = new Services.UninstallRelay();
                using var child = Process.Start(new ProcessStartInfo(
                    MachineInstallExe, (silent ? "/uninstall-silent" : "/uninstall") + " --uninstall-relay " + relay.Name)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                if (child == null) return KillerFamily.Deployment.InstallerExitCodes.Failure;
                if (!relay.AuthenticateChild(child))
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                // The interactive child shows the uninstall confirmation itself, so a time limit
                // here would give up on a user who is still reading it, report Busy, and skip the
                // per-user cleanup below while the child went on to uninstall. Only the silent
                // route, which has no dialog, keeps one.
                if (silent ? !child.WaitForExit(120000) : !WaitForever(child))
                    return KillerFamily.Deployment.InstallerExitCodes.Busy;
                if (child.ExitCode == 0 && Directory.Exists(MachineInstallDir))
                    return KillerFamily.Deployment.InstallerExitCodes.Failure;
                // The elevated child put back the default terminal and folder opener of the
                // account it ran as, which over the shoulder is an administrator's. This is the
                // user who turned them on.
                if (child.ExitCode == 0)
                {
                    RemoveTerminalClass(machine: false, restoreDefault: true);   // DefaultTerminal.cs
                    try { FolderOpenerSetting().TurnOff(); } catch { }
                    // Likewise "Start with Windows": the child removed the Run value from the
                    // administrator's hive, and this user's still names the deleted exe.
                    try { KillerFamily.Deployment.StartupRegistration.Set(Services.AppInfo.DisplayName, false); } catch { }
                }
                return child.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC was declined. Leave the installation untouched.
                return KillerFamily.Deployment.InstallerExitCodes.Cancelled;
            }
            catch (Exception ex)
            {
                string message = string.Format(AppMainWindow.LocStatic("Str_Dlg_UninstallElevateFailed"), ex.Message);
                if (silent) Console.Error.WriteLine(message);
                else new ConfirmDialog(message, null, AppMainWindow.LocStatic("Str_Btn_OK"), installer: true, notification: true).ShowDialog();
                return KillerFamily.Deployment.InstallerExitCodes.FromException(ex);
            }
        }

        /// <summary>
        /// The elevated update swap (see the --apply-update branch in OnStartup). Reads the
        /// download once, checks it against the hash the unelevated window verified, waits for
        /// that window to close, then puts exactly the checked bytes in place of this exe.
        /// </summary>
        private static int ApplyElevatedUpdate(string download, string sha256, string pidText, string version)
        {
            string curExe = Process.GetCurrentProcess().MainModule!.FileName;
            byte[]? data = Services.VerifiedFile.ReadVerified(download, sha256);
            if (data == null) { OpenReleasesUnelevated(); return 1; }

            if (int.TryParse(pidText, out int pid))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.WaitForExit(60_000)) { OpenReleasesUnelevated(); return 1; }
                }
                catch (ArgumentException) { /* already exited */ }
                catch { OpenReleasesUnelevated(); return 1; }
            }

            // Retire the backups earlier updates left here while this process still holds the
            // rights to: the next normal start runs unelevated and cannot delete in Program
            // Files, so each update used to leave a full copy of the previous exe behind. Only
            // backups are touched, and the one that is still running stays for a later pass.
            try { Services.RecoveryImages.Cleanup(curExe, includeUnpublished: false); } catch { }

            // The hash proves these are the bytes the window downloaded. The signature check, on
            // the replacement written beside this exe, proves who published them: a request that
            // reaches this helper some other way cannot put a different publisher's exe here.
            try { Services.VerifiedFile.ReplaceImage(curExe, data, fresh => Services.CodeSignature.MayReplace(curExe, fresh)); }
            catch (Services.PublisherMismatchException)
            {
                try { File.Delete(download); } catch { }
                OpenReleasesUnelevated();
                return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
            }
            catch (Exception ex)
            {
                // Retain both the verified download and any sibling recovery images.
                string report = download + ".recovery.txt";
                try
                {
                    File.WriteAllText(report, ex.Message + Environment.NewLine + "Download: " + download);
                    using (Process.Start("explorer.exe", "/select,\"" + report + "\"")) { }
                }
                catch { }
                OpenReleasesUnelevated();
                return 1;
            }
            try { File.Delete(download); } catch { }

            // Keep Add/Remove Programs on the version actually on disk; a portable copy has no keys.
            string hive = Services.UpdateService.RegistryHiveFor(curExe);
            if (hive.Length > 0 && version.Length > 0)
            {
                RegistryKey root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                try
                {
                    using (var k = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Services.AppInfo.DisplayName))
                    {
                        k?.SetValue("DisplayVersion", version);
                        k?.SetValue("Publisher", "Steve the Killer");
                    }
                    using (var k = root.CreateSubKey(@"Software\" + Services.AppInfo.DisplayName))
                        k?.SetValue("Version", version);
                }
                catch { }
            }

            // Back at the user's own integrity level, not this elevated token.
            try { using (Process.Start("explorer.exe", "\"" + curExe + "\"")) { } } catch { }
            return 0;
        }

        private static bool WaitForever(Process child) { child.WaitForExit(); return true; }

        /// <summary>A failed swap sends the user to the releases page. Through explorer.exe, so the
        /// browser does not inherit this process's administrator token.</summary>
        private static void OpenReleasesUnelevated()
        {
            try { using (Process.Start("explorer.exe", "\"" + Services.UpdateService.ReleasesUrl + "\"")) { } } catch { }
        }

        private static int Uninstall(bool silent, string[] arguments)
        {
            try
            {
                bool machine = string.Equals(Process.GetCurrentProcess().MainModule?.FileName,
                                             MachineInstallExe, StringComparison.OrdinalIgnoreCase);
                bool relayed = arguments.Length == 3 && arguments[1] == "--uninstall-relay";
                if (arguments.Length != 1 && !relayed)
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                if (relayed && (!machine || !KillerFamily.Deployment.InstallerExitCodes.IsElevated()))
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                if (machine) Services.UninstallRemoval.ValidateMachineImage(MachineInstallExe);
                using var coordinator = relayed
                    ? Services.UninstallRelay.ConnectCoordinator(arguments[2], MachineInstallExe) : null;
                int? forwarded = RelaunchMachineUninstallElevatedIfNeeded(machine, silent);
                if (forwarded.HasValue) return forwarded.Value;
                using var uninstallLease = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppName);

                if (!silent)
                {
                    var confirm = new ConfirmDialog(
                        AppMainWindow.LocStatic("Str_Dlg_UninstallConfirm"), null,
                        AppMainWindow.LocStatic("Str_Btn_Uninstall"), installer: true);
                    confirm.ShowDialog();
                    if (!confirm.Confirmed) return KillerFamily.Deployment.InstallerExitCodes.Cancelled;
                }

                const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerShell";
                var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
                var metadata = new Services.UninstallMetadata(hive, RegKey, uninstallKey);
                using var removal = Services.UninstallRemoval.Begin(machine, coordinator);
                try
                {
                    string startMenuDir = machine
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName)
                        : StartMenuDir;
                    string targetDir = machine ? MachineInstallDir : InstallDir;
                    try { File.Delete(Path.Combine(startMenuDir, $"{AppName}.lnk")); }
                    catch (DirectoryNotFoundException) { /* The shortcut's parent is already absent. */ }
                    if (Directory.Exists(startMenuDir) && Directory.GetFileSystemEntries(startMenuDir).Length == 0)
                        Directory.Delete(startMenuDir, recursive: false);
                    try { File.Delete(DesktopLnk); }
                    catch (DirectoryNotFoundException) { /* The desktop folder is already absent. */ }
                    Services.CommandLinePath.UnregisterRequired(targetDir, machine);
                    if (!KillerFamily.Deployment.StartupRegistration.Set(Services.AppInfo.DisplayName, false))
                        throw new UnauthorizedAccessException();
                    if (!UnregisterAssociations(machine)) throw new UnauthorizedAccessException();
                    if (machine) UnregisterAssociations(machine: false);
                    if (machine) TurnOffFolderOpenerForOtherUsers();      // DefaultTerminal.cs
                    // Before RegKey goes: it holds the default terminal pair to put back.
                    RemoveTerminalClass(machine, restoreDefault: true);    // DefaultTerminal.cs
                    if (machine) RemoveTerminalClass(machine: false, restoreDefault: false);
                    hive.DeleteSubKeyTree(RegKey, throwOnMissingSubKey: false);
                    hive.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false);
                    removal.Commit();
                }
                catch (Exception failure)
                {
                    var errors = new System.Collections.Generic.List<Exception> { failure };
                    try { removal.Rollback(failure); } catch (Exception error) { errors.Add(error); }
                    try
                    {
                        metadata.Restore();
                        if (removal.RecoveryImagePath is string recovery)
                        {
                            using var install = hive.CreateSubKey(RegKey);
                            install.SetValue("RecoveryImagePath", recovery);
                            using var entry = hive.CreateSubKey(uninstallKey);
                            entry.SetValue("RecoveryImagePath", recovery);
                        }
                    }
                    catch (Exception error) { errors.Add(error); }
                    if (errors.Count > 1) throw new AggregateException("Uninstall failed; recovery details follow.", errors);
                    throw;
                }
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                return KillerFamily.Deployment.InstallerExitCodes.Success;
            }
            catch (Exception ex)
            {
                string message = string.Format(AppMainWindow.LocStatic("Str_Dlg_UninstallElevateFailed"), ex.Message);
                if (silent) Console.Error.WriteLine(message);
                else new ConfirmDialog(message, null, AppMainWindow.LocStatic("Str_Btn_OK"), installer: true, notification: true).ShowDialog();
                return KillerFamily.Deployment.InstallerExitCodes.FromException(ex);
            }
        }
    }
}
