using System;
using System.Text.RegularExpressions;

// Guesses a document's language from its text, for a document with no extension to go by: an
// untitled tab something was pasted into, or a file named like "hosts". The answer is an
// extension, so the colors come from the same HighlightingManager lookup a saved file uses.
//
// Scored rather than first-match, because the formats overlap: a PowerShell comment and a
// Markdown heading both start with "#", and INI, YAML and PowerShell all have "name = value"
// lines. Each format collects evidence and the clear winner takes it; a near tie or a weak lead
// stays plain text, since wrong colors read worse than none.
namespace KillerShell.Editing
{
    internal static class LanguageSniffer
    {
        // Enough to decide on, small enough to run on every pause in typing.
        private const int SampleLength = 16000;
        private const int MinimumScore = 3;

        private static readonly RegexOptions Opt = RegexOptions.Multiline | RegexOptions.CultureInvariant;

        private static readonly (string Ext, Regex Pattern, int Weight)[] Evidence =
        [
            // PowerShell
            (".ps1", new Regex(@"\b(Get|Set|New|Remove|Write|Invoke|Test|Import|Export|Start|Stop|Add|Out|Select|Where|ForEach|Join|Split|Read|Clear|Copy|Move|Format|ConvertTo|ConvertFrom)-[A-Z][A-Za-z]+", Opt), 2),
            (".ps1", new Regex(@"\$[A-Za-z_][\w:]*\s*=", Opt), 1),
            (".ps1", new Regex(@"\s-(eq|ne|lt|le|gt|ge|like|match|notmatch|contains|and|or|not)\s", Opt | RegexOptions.IgnoreCase), 2),
            (".ps1", new Regex(@"^\s*(param\s*\(|#region\b|#requires\b|\[CmdletBinding)", Opt | RegexOptions.IgnoreCase), 3),
            (".ps1", new Regex(@"\[(string|int|int64|bool|switch|datetime|double|Math)\]", Opt | RegexOptions.IgnoreCase), 2),
            // Batch
            (".bat", new Regex(@"^\s*@?echo\s+(off|on)\b", Opt | RegexOptions.IgnoreCase), 4),
            (".bat", new Regex(@"^\s*(rem\s|::|goto\s|:[A-Za-z_]+\s*$|setlocal\b|if\s+(not\s+)?(exist|errorlevel|defined)\b)", Opt | RegexOptions.IgnoreCase), 2),
            (".bat", new Regex(@"%~?[0-9a-z]*[dpnx]*[0-9]%|%[A-Za-z_]+%", Opt | RegexOptions.IgnoreCase), 1),
            // Registry export
            (".reg", new Regex(@"^(Windows Registry Editor Version|REGEDIT4)", Opt), 8),
            (".reg", new Regex(@"^\[HKEY_[A-Z_]+", Opt), 3),
            // C#
            (".cs", new Regex(@"^\s*using\s+[A-Z][\w.]*;\s*$", Opt), 3),
            (".cs", new Regex(@"^\s*(namespace\s+[\w.]+|(public|private|internal|protected)\s+(static\s+|sealed\s+|partial\s+)*(class|struct|interface|record|enum|void|async)\b)", Opt), 3),
            // Python
            (".py", new Regex(@"^\s*(def\s+\w+\s*\(.*\)\s*(->\s*[\w\[\], .]+)?:|class\s+\w+(\(.*\))?:)\s*$", Opt), 3),
            (".py", new Regex(@"^\s*(import\s+\w+(\.\w+)*|from\s+[\w.]+\s+import\s+)", Opt), 2),
            (".py", new Regex(@"^\s*(elif\s.*:|if\s+__name__\s*==|print\()", Opt), 2),
            // YAML
            (".yml", new Regex(@"^---\s*$", Opt), 2),
            (".yml", new Regex(@"^\s*-\s+[A-Za-z_][\w-]*:\s", Opt), 2),
            (".yml", new Regex(@"^[A-Za-z_][\w-]*:\s*$", Opt), 1),
            // INI
            (".ini", new Regex(@"^\s*\[[A-Za-z][\w .-]*\]\s*$", Opt), 2),
            (".ini", new Regex(@"^\s*[A-Za-z_][\w.-]*\s*=\s*[^=]*$", Opt), 1),
            // Markdown
            (".md", new Regex(@"^#{1,6}\s+\S", Opt), 1),
            (".md", new Regex(@"^```", Opt), 3),
            (".md", new Regex(@"\[[^\]\n]+\]\([^)\s]+\)|\*\*[^*\n]+\*\*", Opt), 2),
        ];

        /// <summary>The extension whose colors fit <paramref name="text"/>, or null for plain text.</summary>
        internal static string? Detect(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string sample = text!.Length > SampleLength ? text.Substring(0, SampleLength) : text;
            string trimmed = sample.TrimStart();

            // Formats whose first character says it outright.
            if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return ".xml";
            if (trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return ".html";
            if (trimmed.StartsWith("<") && Regex.IsMatch(sample, @"</[A-Za-z][\w:.-]*>")) return ".xml";
            if ((trimmed.StartsWith("{") || trimmed.StartsWith("["))
                && Regex.IsMatch(sample, "\"[^\"\\n]+\"\\s*:")) return ".json";

            string? best = null;
            int bestScore = 0, runnerUp = 0;
            foreach (string ext in new[] { ".ps1", ".bat", ".reg", ".cs", ".py", ".yml", ".ini", ".md" })
            {
                int score = 0;
                foreach (var (e, pattern, weight) in Evidence)
                    if (e == ext) score += Math.Min(pattern.Matches(sample).Count, 5) * weight;
                if (score > bestScore) { runnerUp = bestScore; bestScore = score; best = ext; }
                else if (score > runnerUp) runnerUp = score;
            }
            // A clear winner only: plain text beats the wrong colors.
            return bestScore >= MinimumScore && bestScore > runnerUp * 3 / 2 ? best : null;
        }
    }
}
