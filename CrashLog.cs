// ═══════════════════════════════════════════════════════════
//  CRASH LOG  -  last-resort diagnostics
// ═══════════════════════════════════════════════════════════
//
// The same last-resort log KillerNotes and Killendar keep. Until this existed, a failure left
// nothing behind but a Windows Application Error event naming no line of KillerShell, so every
// diagnosis started from "it closed".
//
// The handler deliberately does NOT set e.Handled. Swallowing a dispatcher exception leaves the
// app running in a state nobody reasoned about, and this app moves, deletes and saves files on
// the user's behalf - a file operation from a half-broken window is worse than a crash. The log
// is written, then the failure carries on exactly as it did before. Known, ordinary failures
// are caught where they happen instead (Services/ShellOpen.cs is one).
//
// Everything here is wrapped in catch-all: a crash reporter that throws while reporting turns a
// diagnosable failure into a mystery.

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;

namespace KillerShell
{
    public partial class App : Application
    {
        /// <summary>Under %LOCALAPPDATA%\KillerShell beside the app's other per-machine state: a
        /// crash log belongs on the machine that crashed.</summary>
        private static string CrashLogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName, "crash.log");

        // One file, trimmed when it gets long, rather than a folder that grows forever. The
        // interesting entry is nearly always the last one.
        private const long CrashLogMaxBytes = 256 * 1024;

        /// <summary>Hooks the two places an unhandled exception can surface. Called first thing
        /// in OnStartup so it covers startup itself, which is where the worst ones live.</summary>
        private void HookCrashLogging()
        {
            DispatcherUnhandledException += (_, e) => WriteCrashLog("dispatcher", e.Exception);
            // Background threads: the process is already going down by the time this runs, so
            // there is nothing to do but write.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteCrashLog("appdomain", e.ExceptionObject as Exception);
        }

        internal static void WriteCrashLog(string source, Exception? ex, string? path = null)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("=== ")
                  .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                  .Append("  ").Append(source).Append(" ===\n");
                sb.Append("version: ").Append(VersionString()).Append('\n');
                sb.Append("os: ").Append(Environment.OSVersion.VersionString)
                  .Append(Environment.Is64BitProcess ? " (x64)" : " (x86)").Append('\n');
                sb.Append("clr: ").Append(Environment.Version).Append('\n');
                // ToString() on an Exception already walks the inner chain and prints each
                // stack, which is exactly what is wanted here.
                sb.Append(ex?.ToString() ?? "(no exception object)").Append("\n\n");

                path ??= CrashLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                TrimCrashLog(path);
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* nothing useful is left to do at this point */ }
        }

        /// <summary>Keeps the newest half when the file passes the cap. Cheap, and it cannot
        /// lose the entry about to be written, which is appended after this runs.</summary>
        private static void TrimCrashLog(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= CrashLogMaxBytes) return;
                string text = File.ReadAllText(path);
                int cut = text.IndexOf("\n=== ", text.Length / 2, StringComparison.Ordinal);
                File.WriteAllText(path, cut >= 0 ? text[(cut + 1)..] : "",
                                  new UTF8Encoding(false));
            }
            catch { /* an untrimmable log is still a readable log */ }
        }

        private static string VersionString()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString()
                       ?? "unknown";
            }
            catch { return "unknown"; }
        }
    }
}
