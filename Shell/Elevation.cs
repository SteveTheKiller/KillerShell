using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;
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
namespace KillerShell.Shell
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
            // Already elevated: the whole point of relaunching was to reach an elevated
            // process, and this one already is one. Open the shell tab right here - same
            // placement OpenShell (TerminalTabs.cs) already uses for an unelevated shell -
            // instead of hopping through another UAC prompt and a second window.
            if (IsElevated)
            {
                var target = Pane;                                   // Panes.cs - the focused pane
                var tab = CreateTerminalTabIn(target, profile, folder);
                if (tab == null) return;
                FocusPane(target);                                   // Panes.cs
                ActivateTab(tab);
                return;
            }

            // The trailing slash is stripped so the quote that follows is not read as an
            // escape - but NOT off a drive root, because "C:" is drive-RELATIVE and would
            // resolve against the new process's current directory rather than naming the
            // root. Same trap that sent Up-from-C: back to the home folder.
            string flags = "--shell " + (profile.Skin == TerminalSkin.Lcd ? "cmd" : "pwsh")
                          + " --cwd \"" + TrimForArg(folder) + "\"";

            // Same reuse as RelaunchElevatedProcesses/RelaunchElevatedEventViewer below: an
            // already-elevated window gets the shell tab handed to it instead of a fresh UAC
            // prompt and another window.
            IntPtr existing = FindElevatedKillerShellWindow();
            if (existing != IntPtr.Zero) { SendHandoffArgs(flags, existing); return; }

            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,          // required for the runas verb
                Verb = "runas",
                Arguments = flags,
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
        /// Ctrl+F9: relaunch elevated with the Processes tab already open. Same shape as
        /// RelaunchElevated above, but with nothing to carry across but the flag itself - reuses
        /// "--processes", the same one an unelevated tear-out already hands to a fresh window
        /// (TabTearOut.cs ApplyStartupTearOut), just started with the runas verb instead of
        /// plainly. Nothing happens in THIS window: the request either becomes a second,
        /// elevated window sitting on Processes, or is declined.
        /// </summary>
        internal void RelaunchElevatedProcesses()
        {
            // Already elevated: just open/switch to the tab in THIS window - same as
            // EventViewerRail_Click's own IsElevated check below, and the same reasoning as
            // RelaunchElevated above. No relaunch, no window search, nothing to hand off.
            if (IsElevated) { OpenTaskManager(); return; }   // ProcessTabs.cs

            // Reuse an already-elevated window if one is open, rather than prompting UAC again
            // for a second one that would show the exact same machine-wide process list a
            // heartbeat apart (TabHandoff.cs SendHandoffArgs / FindElevatedKillerShellWindow
            // above).
            IntPtr existing = FindElevatedKillerShellWindow();
            if (existing != IntPtr.Zero) { SendHandoffArgs("--processes", existing); return; }

            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,          // required for the runas verb
                Verb = "runas",
                Arguments = "--processes",
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
        /// Ctrl+F4: relaunch elevated with the Storage Analyzer tab already open, so a scan
        /// can read the folders an ordinary token gets Access Denied on. Same shape as
        /// RelaunchElevatedProcesses above - nothing to carry across but the "--storage" flag,
        /// the same one an unelevated tear-out hands a fresh window (TabTearOut.cs
        /// ApplyStartupTearOut), just started with the runas verb instead of plainly.
        /// </summary>
        internal void RelaunchElevatedStorage()
        {
            // Already elevated: just open/switch to the tab in THIS window.
            if (IsElevated) { OpenStorageAnalyzer(); return; }   // StorageTabs.cs

            // Reuse an already-elevated window if one is open, rather than prompting UAC again.
            IntPtr existing = FindElevatedKillerShellWindow();
            if (existing != IntPtr.Zero) { SendHandoffArgs("--storage", existing); return; }

            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,          // required for the runas verb
                Verb = "runas",
                Arguments = "--storage",
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
        /// Ctrl+F12: relaunch elevated with the Event Viewer tab already open. Same shape as
        /// RelaunchElevatedProcesses above, and for the same reason there is nothing to carry
        /// across but the flag itself - reuses "--eventviewer" the same way ApplyStartupTearOut
        /// (TabTearOut.cs) already reopens a torn-out Event Viewer tab in a fresh window, just
        /// started with the runas verb instead of plainly. There is no unelevated variant of this
        /// call: bare F12 is locked family-wide to the About card, and the Security log this tab
        /// reads refuses to open for a process that is not elevated, so an unelevated Event
        /// Viewer would just be a worse Application/System-only version of Processes' own grid.
        /// Nothing happens in THIS window: the request either becomes a second, elevated window
        /// sitting on Event Viewer, or is declined.
        /// </summary>
        internal void RelaunchElevatedEventViewer()
        {
            // Already elevated: just open/switch to the tab in THIS window. Same check
            // EventViewerRail_Click already makes for its own click path - this is the Ctrl+F12
            // path hitting the exact same gap RelaunchElevatedProcesses had.
            if (IsElevated) { OpenEventViewer(); return; }   // EventViewerTabs.cs

            // Same reuse as RelaunchElevatedProcesses above: an already-elevated window gets the
            // Event Viewer tab handed to it instead of a fresh UAC prompt and another window.
            IntPtr existing = FindElevatedKillerShellWindow();
            if (existing != IntPtr.Zero) { SendHandoffArgs("--eventviewer", existing); return; }

            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,          // required for the runas verb
                Verb = "runas",
                Arguments = "--eventviewer",
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

        /// <summary>
        /// Retry a document save that was refused with access denied - Ctrl+F7's whole point
        /// (EditorTabs.cs SaveActiveEditor / EditorControl.ElevatedSaveOnFail). Same shape as
        /// RecycleElevated above: the document's text is staged to a temp file in this document's
        /// own encoding, a second instance is started elevated and copies that staged file over
        /// the real, permission-denied path, then exits without a window. Nothing happens in
        /// THIS window unless it succeeds - if the prompt is declined, nothing was written.
        /// </summary>
        internal void RetrySaveElevated(SearchTab t, string name)
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe) || t.Editor == null) return;

            string tempFile;
            try
            {
                tempFile = Path.GetTempFileName();
                t.Editor.ExportTextTo(tempFile);
            }
            catch (Exception ex)
            {
                SetTabStatusKey(t, "Str_Ed_SaveFailed", name, ex.Message);
                return;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,          // required for the runas verb
                Verb = "runas",
                Arguments = "--elevated-save \"" + TrimForArg(tempFile) + "\" \""
                          + TrimForArg(t.Editor.FilePath) + "\"",
            };

            try
            {
                var proc = Process.Start(psi);
                if (proc == null) return;

                // Same reasoning as RecycleElevated: the exit code is the only channel back, so
                // a refused write does not silently read as a successful save.
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    int code = proc.ExitCode;
                    proc.Dispose();
                    try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (code == 0)
                        {
                            t.Editor.IsModified = false;
                            SetTabStatusKey(t, "Str_Ed_Saved", name);
                        }
                        else
                        {
                            SetTabStatusKey(t, "Str_Status_ElevatedSaveFailed", name);
                        }
                        SetEditorTitle(t);
                        SyncEditorBar(t);
                    }));
                };
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: declined at the prompt. Same as above, that is an answer -
                // nothing was written, so the tab stays exactly as dirty as it was.
                try { File.Delete(tempFile); } catch { }
            }
            catch (Exception ex)
            {
                try { File.Delete(tempFile); } catch { }
                SetTabStatusKey(t, "Str_Status_ElevateFailed", ex.Message);
            }
        }

        /// <summary>
        /// Ctrl+F11: relaunch elevated with the Registry Editor tab already open. Same shape as
        /// RelaunchElevatedEventViewer above, and for the same reason there is nothing to carry
        /// across but the flag itself - reuses "--registry" the same way ApplyStartupTearOut
        /// (TabTearOut.cs) already reopens a torn-out Registry tab in a fresh window, just started
        /// with the runas verb instead of plainly. There is no unelevated variant of this call at
        /// all: unlike Processes/Performance there is no bare F11 row for this (bare F11 stays the
        /// Performance tab, untouched), and unlike Event Viewer's Application/System logs there is
        /// no partial unelevated experience worth offering either - editing the registry as a
        /// standard user is refused by Windows for most of the tree that matters, so a "working"
        /// unelevated tab would just be a worse, half-broken version of this one. Nothing happens
        /// in THIS window: the request either becomes a second, elevated window sitting on the
        /// Registry Editor, or is declined.
        /// </summary>
        internal void RelaunchElevatedRegistryEditor()
        {
            // Already elevated: just open/switch to the tab in THIS window. Same check
            // EventViewerRail_Click/RelaunchElevatedEventViewer already make for their own paths.
            if (IsElevated) { OpenRegistryEditor(); return; }   // RegistryEditorTabs.cs

            // Same reuse as RelaunchElevatedProcesses/RelaunchElevatedEventViewer above: an
            // already-elevated window gets the Registry Editor tab handed to it instead of a
            // fresh UAC prompt and another window.
            IntPtr existing = FindElevatedKillerShellWindow();
            if (existing != IntPtr.Zero) { SendHandoffArgs("--registry", existing); return; }

            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,          // required for the runas verb
                Verb = "runas",
                Arguments = "--registry",
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

            // elevated: IsElevated, NOT the default false. This process IS the elevated host the
            // relaunch above asked for, so the shell it is here to run is an elevated one and the
            // profile has to say so. Built unelevated, it fell straight through OpenShell's
            // "non-elevated profile in an elevated window" guard (TerminalTabs.cs), which bounced
            // the request back OUT to a fresh unelevated window via explorer.exe - so Ctrl+F8 put
            // up a UAC prompt and then handed back an ordinary window, spawning another
            // explorer.exe and another KillerShell per press, while the admin window it had just
            // created sat there with no terminal in it at all (2026-08-08).
            var profile = string.Equals(kind, "cmd", StringComparison.OrdinalIgnoreCase)
                ? TerminalProfile.Cmd(elevated: IsElevated)
                : TerminalProfile.PowerShell(elevated: IsElevated);

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
            // Visibility from the THEME, not a hard Visible: on 98SE the inner halo is wrong
            // twice over - a ring floating inside a hard rectangular frame, and it left the
            // frame's own black outer line reading as an odd black border around the admin
            // window. ElevationHaloVisibility is Collapsed on a flat theme
            // and Visible everywhere else, and it follows a live theme switch.
            ElevationHalo.SetResourceReference(UIElement.VisibilityProperty, "ElevationHaloVisibility");

            // The flat theme's admin signal instead: a 2px accent band AROUND the window's own
            // gray bevel frame, drawn by the root Border's edge - NOT painted over the frame
            // rings, which swallowed the gray border when tried that way - the regular gray
            // border stays, with the colored ring AROUND it. ElevationEdge* resolve to the
            // accent at 2px on a flat theme and to the window's ordinary WindowEdge values
            // everywhere else, so nothing changes where the halo is the signal.
            WindowFrame.SetResourceReference(Border.BorderBrushProperty, "ElevationEdgeBrush");
            WindowFrame.SetResourceReference(Border.BorderThicknessProperty, "ElevationEdgeThickness");

            // The ring reads as "something is different" from the corner of the eye; the caption
            // is what says WHAT. Accent, same as the ring, so the two are obviously one signal.
            ElevatedTag.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            ElevatedTag.Visibility = Visibility.Visible;

            // The taskbar and Alt+Tab read this, so an admin window is identifiable even when
            // it is not the one you are looking at. Localized (not a hardcoded English literal):
            // the exact same string the custom title bar shows beside the wordmark
            // (MainWindow.xaml ElevatedTag, Str_Title_Elevated), so the one thing a user actually
            // reads and the one thing Windows itself reports for the window agree - and so
            // FindElevatedKillerShellWindow below can find another elevated window's title by
            // matching against this process's own copy of that same string, in whatever locale
            // is active, rather than an English word that would only ever match an en-US window.
            Title = "KillerShell " + Loc("Str_Title_Elevated");
        }

        // ═══════════════════════════════════════════════════════════
        //  REUSE - hand an admin tab request to an already-elevated window instead of
        //  prompting UAC and opening yet another one.
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Any other, already-elevated KillerShell top-level window, or <see cref="IntPtr.Zero"/>
        /// when none is open. Used by RelaunchElevatedProcesses/RelaunchElevatedEventViewer below
        /// so opening a second admin tab does not re-prompt UAC and spawn another window when one
        /// is already sitting there idle - the request is handed off into it instead
        /// (TabHandoff.cs SendHandoffArgs), the same WM_COPYDATA path a dragged tab merges through.
        /// </summary>
        /// <remarks>
        /// Elevation cannot be read off another process's token from here: a non-elevated caller
        /// is denied access to query a higher-integrity process's token, and this caller may
        /// itself be unelevated - that is the whole point, catching the case BEFORE prompting.
        /// Instead this reads the window's own title text, which ApplyElevationHalo above already
        /// stamps with the localized "- Escalated Privileges" suffix (Str_Title_Elevated) the
        /// moment a window comes up elevated, and compares it against THIS process's own
        /// localized copy of that same string - never a hardcoded English literal - so the match
        /// still works whatever locale either window is running in.
        /// </remarks>
        private IntPtr FindElevatedKillerShellWindow()
        {
            string suffix = Loc("Str_Title_Elevated");
            if (string.IsNullOrEmpty(suffix)) return IntPtr.Zero;

            var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            IntPtr found = IntPtr.Zero;

            EnumWindows((hwnd, _) =>
            {
                if (hwnd == self || !IsKillerShellProcessWindow(hwnd)) return true;   // keep looking

                int len = GetWindowTextLength(hwnd);
                if (len == 0) return true;

                var sb = new System.Text.StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                if (sb.ToString().IndexOf(suffix, StringComparison.Ordinal) < 0) return true;

                found = hwnd;
                return false;   // one elevated window is enough - stop enumerating
            }, IntPtr.Zero);

            return found;
        }
    }
}
