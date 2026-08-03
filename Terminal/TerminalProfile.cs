using System;
using System.IO;

// What a terminal tab is: a command line, a skin, and whether it needs elevating.
//
// Resolution happens at OPEN time, not at startup. A field tech's machine gets PowerShell 7
// installed mid-session often enough, and resolving once at launch would mean the app had to
// be restarted before it noticed.
using KillerShell.Shell;

namespace KillerShell.Terminal
{
    internal sealed class TerminalProfile
    {
        public string Name { get; }
        public string CommandLine { get; }
        public TerminalSkin Skin { get; }
        public bool Elevated { get; }

        /// <summary>MDL2 glyph for the tab, so a shell tab is not mistaken for a folder tab.</summary>
        public string Glyph { get; }

        /// <summary>
        /// The resolved exe itself (unquoted, no arguments) - pwsh.exe, powershell.exe or
        /// cmd.exe, wherever Resolve* actually found it. Kept alongside CommandLine (which has
        /// the prompt injection and -NoLogo baked in and cannot be un-quoted reliably) so callers
        /// that want the real file - the tab-strip overflow menu asking IconCache for the
        /// system's own PowerShell/cmd icon (Tabs.cs) - do not have to re-derive it.
        /// </summary>
        public string ExePath { get; }

        private TerminalProfile(string name, string cmd, string exePath, TerminalSkin skin, string glyph, bool elevated)
        {
            Name = name;
            CommandLine = cmd;
            ExePath = exePath;
            Skin = skin;
            Glyph = glyph;
            Elevated = elevated;
        }

        // E756 command prompt, E7BC shield.
        private static readonly string GlyphShell = ((char)0xE756).ToString();
        private static readonly string GlyphAdmin = ((char)0xE7EF).ToString();

        public static TerminalProfile PowerShell(bool elevated = false)
        {
            string exe = ResolvePowerShellExe();

            // KillerShell's own prompt, dot-sourced after the user's $PROFILE has run so it wins
            // in here and nowhere else (MainWindow.PromptArgs, Terminal/PromptScript.cs).
            // Empty when the prompt is switched off, which leaves the command line as it was.
            string prompt = MainWindow.PromptArgs();
            string cmd = exe.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)
                       ? Quote(exe)                          // the ResolveCmd() fallback path
                       : Quote(exe) + " -NoLogo" + prompt;

            return new("PowerShell", cmd, exe, TerminalSkin.Default,
                       elevated ? GlyphAdmin : GlyphShell, elevated);
        }

        public static TerminalProfile Cmd(bool elevated = false)
        {
            string exe = ResolveCmdExe();
            return new("Command Prompt", Quote(exe), exe, TerminalSkin.Lcd,
                       elevated ? GlyphAdmin : GlyphShell, elevated);
        }

        // ═══════════════════════════════════════════════════════════
        //  RESOLUTION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// PowerShell 7 if it is installed, Windows PowerShell otherwise, cmd as the last
        /// resort. pwsh is looked for on PATH and in both Program Files trees rather than only
        /// on PATH, because an MSI install does not always reach the PATH of an already running
        /// session, and this app is often started from a shortcut rather than a shell.
        /// </summary>
        private static string ResolvePowerShellExe()
        {
            string? pwsh = Find("pwsh.exe")
                        ?? InProgramFiles(@"PowerShell\7\pwsh.exe")
                        ?? InProgramFiles(@"PowerShell\7-preview\pwsh.exe");
            if (pwsh != null) return pwsh;

            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                     @"WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(ps)) return ps;

            return ResolveCmdExe();
        }

        private static string ResolveCmdExe()
        {
            string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                      "cmd.exe");
            return File.Exists(cmd) ? cmd : "cmd.exe";
        }

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

        // Checks both trees: a 64 bit pwsh lands in Program Files, a 32 bit one in (x86), and
        // on an x86 process the two environment variables swap meaning.
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

        // Always quoted. "C:\Program Files\PowerShell\7\pwsh.exe" unquoted is the textbook
        // CreateProcess ambiguity, and it is a real path on every machine this runs on.
        private static string Quote(string path) => "\"" + path + "\"";
    }
}
