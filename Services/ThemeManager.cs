using System;
using System.Windows;

namespace KillerShell.Services
{
    public enum Theme { Dark, Light, Black, Blood, Greed, Cyanotic }
    public enum Accent { Green, Red, Blue, Purple, Orange, Teal }

    /// <summary>
    /// KillerUI / Grunge theme engine. Swaps the palette dictionary (MergedDictionaries[0]) in
    /// place at runtime; control styles bind brushes via DynamicResource so an in-place per-key
    /// update repaints everything. Persistence is pluggable (wire GetSetting/SetSetting at startup).
    /// Requires Themes/{Theme}.xaml color dictionaries (copy them from KillerScan) merged at [0].
    /// </summary>
    public static class ThemeManager
    {
        public static Func<string, string?> GetSetting { get; set; } = _ => null;
        public static Action<string, string> SetSetting { get; set; } = (_, _) => { };

        private static Theme _current = Theme.Dark;   // KillerShell default: Dark (Blue accent)
        private static Accent _darkAccent  = Accent.Blue;
        private static Accent _lightAccent = Accent.Green;
        private static Accent _blackAccent = Accent.Orange;

        public static Theme Current => _current;
        public static Accent AccentChoiceFor(Theme t) => AccentFor(t);

        private static Accent AccentFor(Theme t) =>
            t == Theme.Light ? _lightAccent : t == Theme.Black ? _blackAccent : _darkAccent;

        private static bool HasAccents(Theme t) =>
            t == Theme.Dark || t == Theme.Light || t == Theme.Black;

        public static event Action? ThemeChanged;

        /// <summary>Set by App before <see cref="Initialize"/> when the process is running elevated.</summary>
        /// <remarks>
        /// An admin window gets Blood, and keeps its own theme under its own key. Two reasons
        /// for a separate key rather than just forcing the palette: switching theme inside an
        /// admin window must not rewrite the theme every ordinary window uses, and an elevated
        /// process shares the same HKCU as the unelevated one, so a single key would have them
        /// fighting over it. Blood is the default rather than a lock - the point is that an
        /// admin window never comes up looking like a user-level one.
        /// </remarks>
        public static bool Elevated { get; set; }

        private static string ThemeKey => Elevated ? "ThemeAdmin" : "Theme";

        public static void Initialize()
        {
            _current     = Enum.TryParse<Theme>(GetSetting(ThemeKey),       out var t)  ? t  : (Elevated ? Theme.Blood : _current);
            _darkAccent  = Enum.TryParse<Accent>(GetSetting("DarkAccent"),  out var da) ? da : _darkAccent;
            _lightAccent = Enum.TryParse<Accent>(GetSetting("LightAccent"), out var la) ? la : _lightAccent;
            _blackAccent = Enum.TryParse<Accent>(GetSetting("BlackAccent"), out var ba) ? ba : _blackAccent;
            LoadDict(_current);
        }

        public static void Apply(Theme theme)
        {
            _current = theme;
            SetSetting(ThemeKey, theme.ToString());
            LoadDict(theme);
            ThemeChanged?.Invoke();
        }

        public static void ApplyAccent(Theme family, Accent accent)
        {
            if      (family == Theme.Light) { _lightAccent = accent; SetSetting("LightAccent", accent.ToString()); }
            else if (family == Theme.Black) { _blackAccent = accent; SetSetting("BlackAccent", accent.ToString()); }
            else                            { _darkAccent  = accent; SetSetting("DarkAccent",  accent.ToString()); }

            if (_current == family)
            {
                LoadDict(_current);
                ThemeChanged?.Invoke();
            }
        }

        private static void LoadDict(Theme theme)
        {
            // Build the combined dictionary (base theme + accent overlay) OFF the live tree
            // first, then publish it in ONE assignment. Setting merged[0] to a brand-new
            // ResourceDictionary fires exactly one resource-invalidation pass across the whole
            // app; the old code instead did existing[key] = newDict[key] in a loop, which fires
            // a SEPARATE invalidation pass per key. With ~150+ keys in a theme dictionary (this
            // app has ANSI terminal colors, editor syntax colors, registry/event-viewer colors
            // on top of the usual chrome brushes - a lot more than a page-viewer app's palette),
            // that was 150+ passes over the whole visual tree on every single theme click - the
            // "HUGE lag... not instant like KillerPDF" Steve reported, 2026-08-02. Confirmed by
            // him as reproducing even with zero terminal/editor tabs open, which ruled out
            // RefreshTerminalThemes/RefreshEditorThemes and pointed straight at this loop.
            var combined = new ResourceDictionary();
            var newDict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{theme}.xaml") };
            foreach (object key in newDict.Keys)
                combined[key] = newDict[key];

            var accent = AccentFor(theme);
            if (HasAccents(theme) && accent != Accent.Green)
            {
                string family = theme == Theme.Light ? "Light" : theme == Theme.Black ? "Black" : "Dark";
                try
                {
                    var accentDict = new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/Themes/Accents/{family}/{accent}.xaml")
                    };
                    foreach (object key in accentDict.Keys)
                        combined[key] = accentDict[key];
                }
                catch { /* overlay not present - base theme stands */ }
            }

            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > 0)
                merged[0] = combined;
            else
                merged.Add(combined);
        }
    }
}
