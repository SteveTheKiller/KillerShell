using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;

// What a shell running inside KillerShell is allowed to know about the window hosting it.
// Partial of MainWindow.
//
// This is the handshake every custom-prompt idea rests on. A prompt that can read which theme
// the window is wearing can match it, which is the one thing no standalone terminal can do -
// KillerShell owns both the palette and the shell, so the two can agree.
//
// TWO channels, because they answer different questions:
//
//   ENVIRONMENT (KS_*)  - set on THIS process, which ConPty.Launch passes down by inheritance
//                         (lpEnvironment is IntPtr.Zero, so the child gets a copy of ours).
//                         A copy is the point and also the limit: a shell that is already open
//                         holds the values it was born with and nothing can change them from
//                         out here. So the environment carries what does not move - session,
//                         version, admin - plus the theme AT SPAWN, which is what a one-shot
//                         banner wants.
//
//   STATE FILE          - a few lines of KEY=VALUE that get rewritten on every theme or accent
//                         switch. A prompt function re-reads it each render, so an ALREADY OPEN
//                         shell recolors the moment the window does. Plain text rather than
//                         JSON so parsing it is two lines of PowerShell with no dependency, and
//                         no ConvertFrom-Json on the hot path of every prompt.
//
// Colors are handed over as the resolved hex the window is actually painting, not as theme
// names. A prompt should not have to carry its own copy of six palettes and keep them in step
// with Themes/*.xaml; it asks what red means today and gets an answer.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Where the live state file lives. Keyed by process id, so two KillerShell windows -
        /// or an ordinary one and an elevated one - never write over each other's state.
        /// </summary>
        private static string ShellStatePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KillerShell", "session", Process.GetCurrentProcess().Id + ".env");

        // Named for this file rather than the obvious AppVersion: Modules.cs already owns that
        // name on this same partial class, and it answers a different question - the four-part
        // assembly version it keys its unpack folder by. This is the version the titlebar
        // shows, which is the one a banner should print.
        private static string ShellVersion =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";

        // The palette keys a prompt has any business knowing, paired with the KS_ name it is
        // published under. Deliberately short of the full theme dictionary: these are the seven
        // roles a prompt actually uses, and every one of them exists in all six themes.
        private static readonly (string Key, string Res)[] PromptColors =
        [
            // The first three are the TERMINAL's own, not the app's. A prompt renders on the
            // console surface, so it has to be colored for that surface - on 98SE the app is
            // light gray and the console is black, and publishing the app's #000000 text and
            // #004f00 accent made the prompt invisible against it (2026-08-08). All three
            // default to the app's values, so nothing changes on a theme that does not override
            // its console.
            ("ACCENT", "TerminalAccentBrush"),
            ("FG",     "TerminalForegroundBrush"),
            ("BG",     "TerminalBackgroundBrush"),
            ("MUTED",  "MutedTextBrush"),
            ("DIM",    "DimTextBrush"),
            ("OK",     "OkBrush"),
            ("WARN",   "WarnBrush"),
        ];

        // ═══════════════════════════════════════════════════════════
        //  SETUP
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Publish the environment and write the first state file. Subscribes to the theme so
        /// nothing downstream has to remember to call the refresh - a switch anywhere, from the
        /// flyout or from a future keyboard shortcut, updates the shells for free.
        /// </summary>
        internal void InitShellEnv()
        {
            PruneDeadSessions();
            RefreshShellEnv();
            Services.ThemeManager.ThemeChanged += RefreshShellEnv;
        }

        /// <summary>Republish everything. Cheap enough to run on every theme switch.</summary>
        private void RefreshShellEnv()
        {
            string state = ShellStatePath;

            SetVar("KS_SESSION", Process.GetCurrentProcess().Id.ToString());
            SetVar("KS_VERSION", ShellVersion);
            SetVar("KS_ADMIN",   IsElevated ? "1" : "0");
            SetVar("KS_THEME",   Services.ThemeManager.Current.ToString());
            SetVar("KS_ACCENT",  Hex("PrimaryBrush"));
            SetVar("KS_STATE",   state);

            WriteState(state);
        }

        /// <summary>
        /// Drop the state file on the way out. Best effort: a crash leaves one behind, which is
        /// what PruneDeadSessions is for.
        /// </summary>
        internal void DisposeShellEnv()
        {
            Services.ThemeManager.ThemeChanged -= RefreshShellEnv;
            try { File.Delete(ShellStatePath); } catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  STATE FILE
        // ═══════════════════════════════════════════════════════════
        private void WriteState(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var sb = new StringBuilder();
                sb.Append("THEME=").Append(Services.ThemeManager.Current).Append('\n');
                sb.Append("ADMIN=").Append(IsElevated ? "1" : "0").Append('\n');
                sb.Append("VERSION=").Append(ShellVersion).Append('\n');
                sb.Append("SESSION=").Append(Process.GetCurrentProcess().Id).Append('\n');

                foreach (var (Key, Res) in PromptColors)
                    sb.Append(Key).Append('=').Append(Hex(Res)).Append('\n');

                // Written whole, in one call: a prompt reading this file mid-write would
                // otherwise see half a palette and paint itself in whatever survived.
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* a prompt that cannot read the file falls back to plain colors */ }
        }

        /// <summary>
        /// Clear out state files belonging to processes that are gone. Without this the folder
        /// accumulates one file per crash, forever, and a prompt could be handed the palette of
        /// a window that no longer exists.
        /// </summary>
        private static void PruneDeadSessions()
        {
            try
            {
                string dir = Path.GetDirectoryName(ShellStatePath)!;
                if (!Directory.Exists(dir)) return;

                foreach (var f in Directory.GetFiles(dir, "*.env"))
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out int pid)) continue;
                    if (pid == Process.GetCurrentProcess().Id) continue;

                    try { Process.GetProcessById(pid); }        // still alive: leave it alone
                    catch (ArgumentException) { try { File.Delete(f); } catch { } }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════
        // Process scope, not User or Machine: these describe THIS window and must not outlive
        // it or leak into unrelated programs. Process scope is also what CreateProcess copies.
        private static void SetVar(string name, string value)
        {
            try { Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process); }
            catch { }
        }

        /// <summary>
        /// A theme brush as #rrggbb. Alpha is dropped: these end up in ANSI escapes, which have
        /// no notion of transparency, and a prompt asking for a color wants the one it will see.
        /// </summary>
        private static string Hex(string resourceKey)
        {
            try
            {
                if (Application.Current.TryFindResource(resourceKey) is SolidColorBrush b)
                {
                    var c = b.Color;
                    return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
                }
            }
            catch { }
            return "#ffffff";
        }
    }
}
