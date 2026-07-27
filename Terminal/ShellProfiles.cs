using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

// Where a PowerShell host keeps its $PROFILE - asked, never assumed.
//
// THE WHOLE POINT OF THIS FILE: the path is not Documents\PowerShell\Microsoft.PowerShell_profile.ps1.
// It is that only on a machine whose Documents folder is where Windows first put it. Move
// Documents to D:, or - far more common on a corporate build - let OneDrive redirect it, and the
// real path is under OneDrive with the org name in it. Every tool that hardcodes the Documents
// path silently edits a file the shell will never load, and the user is left wondering why their
// profile does nothing. On a fleet where Known Folder Move is policy, that is most machines.
//
// So the shell binary is asked. `$PROFILE.CurrentUserCurrentHost` is PowerShell's own answer to
// the question and is correct by construction on every machine, redirected or not.
//
// CurrentUserCurrentHost rather than CurrentUserAllHosts: the per-host file is the one that runs
// in a console session and the one people mean by "my profile". AllHosts also loads inside the
// ISE and VS Code, and quietly editing the file that changes those too is not what an Edit
// profile row should do.
//
// The two hosts are separate profiles, deliberately shown as separate rows. PowerShell 7 and
// Windows PowerShell do NOT share one - 7 reads Documents\PowerShell, 5.1 reads
// Documents\WindowsPowerShell - and half the "why is my profile not loading" calls in the world
// are somebody editing one and starting the other.
namespace KillerShell.Terminal
{
    /// <summary>One installed PowerShell host, and where its per-host profile lives.</summary>
    internal sealed class ShellHost
    {
        internal string Name { get; }
        internal string Exe  { get; }

        internal ShellHost(string name, string exe) { Name = name; Exe = exe; }
    }

    internal static class ShellProfiles
    {
        /// <summary>
        /// The PowerShell hosts actually installed, newest first. Cheap - this only looks for
        /// files, so it is safe to call while a menu is opening.
        /// </summary>
        /// <remarks>
        /// Resolved every time rather than cached, for the same reason TerminalProfile resolves
        /// at open time: a field tech's machine gets PowerShell 7 installed mid-session often
        /// enough, and a list built at launch would keep hiding the row that just became true.
        /// </remarks>
        internal static List<ShellHost> Installed()
        {
            var hosts = new List<ShellHost>(2);

            string? pwsh = Find("pwsh.exe")
                        ?? InProgramFiles(@"PowerShell\7\pwsh.exe")
                        ?? InProgramFiles(@"PowerShell\7-preview\pwsh.exe");
            if (pwsh != null) hosts.Add(new ShellHost("PowerShell 7", pwsh));

            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                     @"WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(ps)) hosts.Add(new ShellHost("Windows PowerShell", ps));

            return hosts;
        }

        /// <summary>
        /// Ask <paramref name="host"/> where its profile is. Empty when it could not be asked.
        /// </summary>
        /// <remarks>
        /// -NoProfile so the probe cannot be slowed down, or broken, by whatever is IN the
        /// profile - a script that prompts on load would otherwise hang this call forever.
        /// -NonInteractive and a redirected stdin for the same reason.
        ///
        /// Called on CLICK rather than while the menu is opening: this starts a process, which
        /// costs a few hundred milliseconds on a warm machine and rather more on a cold one, and
        /// a menu that takes half a second to appear reads as the app hanging. The rows come
        /// from Installed(), which only stats files.
        /// </remarks>
        internal static string PathFor(ShellHost host)
        {
            try
            {
                var psi = new ProcessStartInfo(host.Exe)
                {
                    Arguments              = "-NoProfile -NonInteractive -Command \"$PROFILE.CurrentUserCurrentHost\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    RedirectStandardInput  = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };

                using var p = Process.Start(psi);
                if (p == null) return string.Empty;

                p.StandardInput.Close();
                string output = p.StandardOutput.ReadToEnd();

                // Bounded. A host that never exits must not take the window with it, and five
                // seconds is far longer than a -NoProfile start has ever needed.
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { }
                    return string.Empty;
                }

                string path = output.Trim();

                // Sanity, not trust: this is the output of another process, and a hooked
                // Write-Host or a banner from a machine-wide profile could put anything on
                // stdout. Only an absolute path to a .ps1 is worth acting on.
                if (path.Length == 0 || path.IndexOf('\n') >= 0) return string.Empty;
                if (!path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return string.Empty;
                if (!Path.IsPathRooted(path)) return string.Empty;

                return path;
            }
            catch { return string.Empty; }
        }

        // ═══════════════════════════════════════════════════════════
        //  LOOKUP
        // ═══════════════════════════════════════════════════════════
        // Copies of TerminalProfile's two finders rather than calls into them: those are private
        // to that class because it resolves a COMMAND LINE, and making them shared would tie the
        // "which shell do we launch" decision to the "which profiles exist" one. They answer the
        // same question today and are free to stop.

        /// <summary>First hit for <paramref name="exe"/> on PATH, or null.</summary>
        private static string? Find(string exe)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in path.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string full;
                    try { full = Path.Combine(dir.Trim(), exe); }
                    catch { continue; }          // a malformed PATH entry, and there is always one
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
            return null;
        }

        // Both trees: a 64 bit pwsh lands in Program Files, a 32 bit one in (x86), and on an x86
        // process the two environment variables swap meaning.
        private static string? InProgramFiles(string relative)
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramW6432"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string full = Path.Combine(root!, relative);
                if (File.Exists(full)) return full;
            }
            return null;
        }
    }
}
