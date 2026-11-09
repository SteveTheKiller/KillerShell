using System;

namespace KillerShell.Shell
{
    /// <summary>
    /// Write an export, then open it. Opening is a courtesy: a machine with no browser
    /// association still got its file, so a failed open never turns into "export failed".
    /// </summary>
    internal static class ExportAndOpen
    {
        internal readonly struct Outcome(bool exported, string? error)
        {
            internal bool Exported { get; } = exported;
            internal string? Error { get; } = error;
        }

        internal static Outcome Run(Action write, Action open)
        {
            try { write(); }
            catch (Exception ex) { return new Outcome(false, ex.Message); }
            try { open(); } catch { }
            return new Outcome(true, null);
        }
    }
}
