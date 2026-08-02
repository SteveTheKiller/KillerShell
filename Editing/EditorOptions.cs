using System;
using System.Globalization;
using System.Windows.Controls;

// Editor settings: one set, app-wide, sitting behind the gear on the document bar.
//
// App-wide rather than per document, deliberately. Every one of these is a statement about how
// the user reads code, not about a particular file - a per-file version would mean the same
// script came back looking different depending on which tab you happened to open it in. The two
// things that ARE properties of the file, encoding and line ending, are readouts on that bar and
// are not settings at all.
//
// Read through a static constructor rather than an Init call wired into startup: they are needed
// the first time an editor is built, which is the first moment they can matter, and the runtime
// already guarantees that happens once and before anything reads one.
using KillerShell.Shell;

namespace KillerShell.Editing
{
    internal static class EditorOptions
    {
        private const string KeyWrap        = "EditorWordWrap";
        private const string KeyLineNumbers = "EditorLineNumbers";
        private const string KeyCurrentLine = "EditorCurrentLine";
        private const string KeyWhitespace  = "EditorWhitespace";
        private const string KeySpaces      = "EditorSpacesForTabs";
        private const string KeyIndent      = "EditorIndentSize";
        private const string KeyFontSize    = "EditorFontSize";

        /// <summary>
        /// Settings key for the editor's font FAMILY.
        /// </summary>
        /// <remarks>
        /// The family and the size are both set from the Fonts dialog, beside the app and
        /// terminal slots (Fonts.cs) - a font picker belongs with the other font pickers, not
        /// buried in a gear on one surface. This class still owns the values, because it is what
        /// pushes them onto an editor; the dialog only writes them.
        /// </remarks>
        internal const string KeyFontFamily = "FontEditor";

        /// <summary>Same range as the terminal's font slider, so the two cannot disagree.</summary>
        internal const double FontMin = 8, FontMax = 28, FontDefault = 12;

        internal static bool   WordWrap      { get; set; }
        internal static bool   LineNumbers   { get; set; }
        internal static bool   CurrentLine   { get; set; }
        internal static bool   Whitespace    { get; set; }
        internal static bool   SpacesForTabs { get; set; }
        internal static int    IndentSize    { get; set; }
        internal static double FontSize      { get; set; }

        /// <summary>Chosen family, or empty to follow the app's MonoFont slot.</summary>
        internal static string FontFamily    { get; set; } = string.Empty;

        // Each default is spelled as "unless the setting says otherwise" in the direction that
        // makes an untouched machine behave the way it should, rather than as unset-means-false.
        static EditorOptions()
        {
            WordWrap      = Get(KeyWrap)        != "0";
            LineNumbers   = Get(KeyLineNumbers) != "0";
            CurrentLine   = Get(KeyCurrentLine) != "0";
            Whitespace    = Get(KeyWhitespace)  == "1";
            SpacesForTabs = Get(KeySpaces)      != "0";
            IndentSize    = ClampIndent(ParseInt(Get(KeyIndent), 4));
            FontSize      = ClampFont(ParseDouble(Get(KeyFontSize), FontDefault));
            FontFamily    = Get(KeyFontFamily);
        }

        /// <summary>Push the current set onto one editor.</summary>
        internal static void Apply(EditorControl editor)
        {
            editor.WordWrap        = WordWrap;
            editor.ShowLineNumbers = LineNumbers;
            editor.FontSize        = FontSize;

            // Empty means "no editor font chosen". That falls back to the app's preferred
            // monospaced face when this machine has it (Fonts.cs), and to the MonoFont resource
            // otherwise - by REFERENCE, so it keeps following that slot afterwards rather than
            // freezing on whatever the app font happened to be when the tab opened.
            var family = Resolve(FontFamily) ?? Resolve(MainWindow.DefaultMonoFont);
            if (family == null) editor.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
            else                editor.FontFamily = family;

            editor.Options.HighlightCurrentLine = CurrentLine;
            editor.Options.ConvertTabsToSpaces  = SpacesForTabs;
            editor.Options.IndentationSize      = IndentSize;

            // One toggle drives both: a file indented with a mix of the two is exactly the case
            // you turn this on to find, so showing one without the other would hide the answer.
            editor.Options.ShowSpaces = Whitespace;
            editor.Options.ShowTabs   = Whitespace;
        }

        /// <summary>Write the current set back. Called after every change from the gear.</summary>
        internal static void Save()
        {
            Set(KeyWrap,        WordWrap      ? "1" : "0");
            Set(KeyLineNumbers, LineNumbers   ? "1" : "0");
            Set(KeyCurrentLine, CurrentLine   ? "1" : "0");
            Set(KeyWhitespace,  Whitespace    ? "1" : "0");
            Set(KeySpaces,      SpacesForTabs ? "1" : "0");
            Set(KeyIndent,      IndentSize.ToString(CultureInfo.InvariantCulture));
            Set(KeyFontSize,    FontSize.ToString("0.##", CultureInfo.InvariantCulture));
            Set(KeyFontFamily,  FontFamily);
        }

        // Named Resolve rather than reused from Fonts.cs: that one is a private static on
        // MainWindow, and an uninstalled family has to fall back rather than throw either way.
        private static System.Windows.Media.FontFamily? Resolve(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try { return new System.Windows.Media.FontFamily(value); }
            catch { return null; }
        }

        internal static double ClampFont(double v) => Math.Round(Math.Max(FontMin, Math.Min(FontMax, v)));

        // 2, 4 or 8. Anything else came from a hand-edited settings file and would put the gear's
        // three buttons in a state none of them can show.
        internal static int ClampIndent(int v) => v == 2 || v == 8 ? v : 4;

        private static string Get(string key) => Services.ThemeManager.GetSetting(key) ?? string.Empty;
        private static void Set(string key, string value) => Services.ThemeManager.SetSetting(key, value);

        private static int ParseInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        // InvariantCulture both ways, like the terminal size: a value written on a comma-decimal
        // machine has to still parse if the settings file is carried to a period-decimal one.
        private static double ParseDouble(string s, double fallback) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }
}
