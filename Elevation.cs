using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Terminal;

// Running as admin: getting there, and making it obvious. Partial of MainWindow.
//
// An elevated process cannot attach to an unelevated pseudoconsole. That is a UAC integrity
// boundary rather than a gap in the API: CreateProcess can pass an attribute list but cannot
// request elevation, and ShellExecuteEx can request elevation but cannot pass an attribute
// list. There is no combination that gets both, which is why Windows Terminal opens a separate
// elevated window for this too.
//
// So an admin shell relaunches KILLERSHELL elevated and lets that instance host it. Everything
// the elevated instance spawns is elevated for free, because the whole process is.
namespace KillerShell
{
    public partial class MainWindow
    {
        /// <summary>True when this whole process is running elevated.</summary>
        internal static bool IsElevated { get; } = CheckElevated();

        private static bool CheckElevated()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ═══════════════════════════════════════════════════════════
        //  RELAUNCH
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Ask for elevation and hand the shell to the new instance. Nothing happens in THIS
        /// window: the request either becomes a second, elevated window or is declined.
        /// </summary>
        internal void RelaunchElevated(TerminalProfile profile, string folder)
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,          // required for the runas verb
                Verb = "runas",
                // The trailing slash is stripped so the quote that follows is not read as an
                // escape - but NOT off a drive root, because "C:" is drive-RELATIVE and would
                // resolve against the new process's current directory rather than naming the
                // root. Same trap that sent Up-from-C: back to the home folder.
                Arguments = "--shell " + (profile.Skin == TerminalSkin.Lcd ? "cmd" : "pwsh")
                          + " --cwd \"" + TrimForArg(folder) + "\"",
                WorkingDirectory = folder,
            };

            try
            {
                Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the user said no at the prompt. That is an answer, not a
                // failure, so it passes silently - they know what they just clicked.
            }
            catch (Exception ex)
            {
                SetTabStatusKey(_active, "Str_Status_ElevateFailed", ex.Message);
            }
        }

        /// <summary>
        /// Retry a recycle the unelevated process was refused. Same shape as the shell relaunch
        /// above: a second instance is started elevated, does the one job it was given and exits.
        /// Nothing happens in THIS window - if the prompt is declined, nothing was deleted.
        /// </summary>
        internal void RecycleElevated(System.Collections.Generic.IReadOnlyList<string> paths)
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe) || paths.Count == 0) return;

            var args = new System.Text.StringBuilder("--recycle");
            foreach (string p in paths) args.Append(" \"").Append(TrimForArg(p)).Append('"');

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,          // required for the runas verb
                Verb = "runas",
                Arguments = args.ToString(),
            };

            try
            {
                var proc = Process.Start(psi);
                if (proc == null) return;

                // The helper has no UI, so its exit code is the only thing it can tell us. Left
                // unwatched, a delete that Controlled Folder Access refused looked exactly like
                // one that worked: a UAC prompt, then nothing, and the file still there.
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    int code = proc.ExitCode;
                    proc.Dispose();
                    if (code == 0) return;
                    Dispatcher.BeginInvoke((Action)(() =>
                        SetTabStatusKey(_active, "Str_Status_ElevatedDeleteFailed")));
                };
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: declined at the prompt. Same as above, that is an answer.
            }
            catch (Exception ex)
            {
                SetTabStatusKey(_active, "Str_Status_ElevateFailed", ex.Message);
            }
        }

        /// <summary>A path safe to quote on a command line: no trailing slash unless it is a root.</summary>
        private static string TrimForArg(string folder)
        {
            if (folder.Length <= 3) return folder;                  // "C:\" and shorter stay whole
            return folder.TrimEnd('\\');
        }

        /// <summary>
        /// Act on --shell / --cwd from an elevated relaunch. Called once the window is up,
        /// because opening a shell needs the panes to exist.
        /// </summary>
        internal void ApplyStartupShell()
        {
            var args = Environment.GetCommandLineArgs();
            string? kind = null, cwd = null;

            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--shell", StringComparison.OrdinalIgnoreCase)) kind = args[i + 1];
                else if (string.Equals(args[i], "--cwd", StringComparison.OrdinalIgnoreCase)) cwd = args[i + 1];
            }

            // --cwd on its own is a plain new window (NewWindow.cs) asking to open where the
            // window it came from was. No shell, no bare layout - just land in the folder.
            if (kind == null)
            {
                if (!string.IsNullOrEmpty(cwd) && System.IO.Directory.Exists(cwd))
                    _ = NavigateTo(cwd!);     // Browse.cs
                return;
            }

            var profile = string.Equals(kind, "cmd", StringComparison.OrdinalIgnoreCase)
                ? TerminalProfile.Cmd()
                : TerminalProfile.PowerShell();

            OpenStartupShell(profile, cwd);   // TerminalTabs.cs
        }

        // ═══════════════════════════════════════════════════════════
        //  HALO
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Ring the whole window in the accent when it is elevated, so an admin window is
        /// never mistaken for an ordinary one.
        /// </summary>
        /// <remarks>
        /// Driven off the process's real token rather than off the command-line flag, so a
        /// window you started as administrator yourself is marked too, however it was started.
        /// </remarks>
        internal void ApplyElevationHalo()
        {
            if (!IsElevated) return;

            // SetResourceReference, not a brush snapshot: the accent is switchable at runtime
            // and a snapshot would leave the halo on the old color after a theme change.
            ElevationHalo.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
            ElevationHaloInner.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
            ElevationHalo.Visibility = Visibility.Visible;

            // The ring reads as "something is different" from the corner of the eye; the caption
            // is what says WHAT. Accent, same as the ring, so the two are obviously one signal.
            ElevatedTag.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            ElevatedTag.Visibility = Visibility.Visible;

            // The taskbar and Alt+Tab read this, so an admin window is identifiable even when
            // it is not the one you are looking at.
            Title = "KillerShell [Administrator]";
        }
    }
}
