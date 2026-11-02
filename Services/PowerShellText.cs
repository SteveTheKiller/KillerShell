using System.Text.RegularExpressions;

namespace KillerShell.Services
{
    internal static class PowerShellText
    {
        /// <summary>The text of a single-quoted PowerShell literal, without the outer quotes.
        /// PowerShell ends a literal at the curly single quotes too, so every form is doubled.</summary>
        internal static string Escape(string value) => Regex.Replace(value, "['‘’‚‛]", "$0$0");

        /// <summary>A single-quoted PowerShell literal that reads back as exactly <paramref name="value"/>.</summary>
        internal static string Literal(string value) => "'" + Escape(value) + "'";
    }
}
