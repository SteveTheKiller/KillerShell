using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KillerShell.Models;

namespace KillerShell.Services
{
    /// <summary>
    /// The CSV the results Export writes (Shell/Export.cs). Column headers stay English
    /// (machine-readable) per project convention.
    /// </summary>
    internal static class CsvExport
    {
        internal const string Header = "Name,Folder,SizeBytes,Modified,Found";

        internal static string Build(IEnumerable<SearchResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var r in results)
            {
                string found = string.Join("; ", r.Matches.Select(m =>
                    m.Lines.Count > 0 ? $"{m.Term.ModeName} ({m.Lines.Count})" : m.Term.ModeName));
                sb.AppendLine(string.Join(",",
                    Text(r.FileName), Text(r.Directory), r.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    Text(r.ModifiedLabel), Text(found)));
            }
            return sb.ToString();
        }

        /// <summary>
        /// One quoted text cell. A cell a spreadsheet would read as a formula starts with an
        /// apostrophe so it opens as the literal text; quoting alone does not stop a spreadsheet
        /// from evaluating it (OWASP, CSV injection).
        /// </summary>
        /// <remarks>
        /// File names may legally start with =, +, - or @, and LibreOffice Calc evaluates an
        /// =1+1 cell from this file on a plain open. The apostrophe shows as part of the text in
        /// LibreOffice and is the documented spreadsheet convention for literal text. The size
        /// column is written as a number and never gets one.
        /// </remarks>
        internal static string Text(string s)
        {
            if (StartsLikeFormula(s)) s = "'" + s;
            return $"\"{s.Replace("\"", "\"\"")}\"";
        }

        private static bool StartsLikeFormula(string s)
        {
            if (s.Length == 0) return false;
            if (s[0] == '\t' || s[0] == '\r') return true;
            int i = 0;
            while (i < s.Length && s[i] == ' ') i++;   // spreadsheets can trim leading spaces on import
            if (i == s.Length) return false;
            // The fullwidth forms are included because some spreadsheets fold them to ASCII.
            char c = s[i];
            return c == '=' || c == '+' || c == '-' || c == '@'
                || c == (char)0xFF1D || c == (char)0xFF0B || c == (char)0xFF0D || c == (char)0xFF20;
        }
    }
}
