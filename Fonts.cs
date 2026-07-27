using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// Font slots. Partial of MainWindow.
//
// Two surfaces have a face worth choosing: the APP, which is the mono family the search and
// results UI is set in (the MonoFont resource), and the TERMINAL, which is separate because a
// shell wants box drawing and powerline glyphs that a general UI font has no reason to carry.
//
// The app slot works by overriding the MonoFont resource at Application level, so every usage
// repaints live and clearing the override falls back to whatever AppStyles.xaml shipped. The
// terminal slot is a plain setting the control reads, because the terminal resolves its own
// GlyphTypeface rather than binding to a resource - it needs the advance width, not a family.
//
// A readability guard keeps symbol fonts out of both: Wingdings as your file list is funny
// exactly once.
namespace KillerShell
{
    /// <summary>A row in the font combos. Public so the ItemTemplate can bind to it.</summary>
    public sealed class FontChoice
    {
        public string Display { get; set; } = string.Empty;

        /// <summary>Empty means "the shipped default".</summary>
        public string Value { get; set; } = string.Empty;

        public FontFamily? Fam { get; set; }
        public override string ToString() => Display;
    }

    public partial class MainWindow
    {
        private const string SetFontApp      = "FontApp";
        private const string SetFontTerm     = "FontTerminal";
        private const string SetFontTermSize = "FontTerminalSize";

        // Matches TerminalControl's own clamp, so the slider cannot ask for a size the control
        // would silently refuse and then disagree with the label.
        internal const double TermSizeMin = 8, TermSizeMax = 28, TermSizeDefault = 12;

        /// <summary>
        /// The face the terminal and the editor use when the user has not picked one.
        /// </summary>
        /// <remarks>
        /// A Nerd Font, because the shipped prompt draws powerline separators and a git branch
        /// glyph and neither exists in a stock console face - on Cascadia they arrive as boxes.
        /// It is a bitmap-style face at 12pt, which is what the terminal and a document both
        /// want and what the results list does not, so it is these two slots and not the app one.
        /// </remarks>
        private const string PreferredMonoFont = "ProFont IIx Nerd Font";

        /// <summary>
        /// <see cref="PreferredMonoFont"/> when it is installed here, empty otherwise.
        /// </summary>
        /// <remarks>
        /// Gated on being installed rather than named unconditionally: this app ships to
        /// machines that have never heard of it, and a default pointing at a missing family
        /// would give every one of them the fallback chain with a dead first entry - no error
        /// anybody could act on, just a font they did not choose failing quietly.
        /// </remarks>
        internal static string DefaultMonoFont { get; private set; } = string.Empty;

        private bool _fontsBuilt;
        private bool _fontsSyncing;

        /// <summary>
        /// The terminal's chosen family, or empty for its own fallback chain. Static because
        /// TerminalControl reads it while building a GlyphTypeface, with no window to ask.
        /// </summary>
        internal static string TerminalFontFamily { get; private set; } = string.Empty;

        /// <summary>
        /// Point size new shells start at. Static for the same reason as the family: the
        /// control reads it in its constructor. Ctrl+wheel inside a terminal still overrides
        /// this for that one tab without writing the setting back.
        /// </summary>
        internal static double TerminalFontSize { get; private set; } = TermSizeDefault;

        // ═══════════════════════════════════════════════════════════
        //  STARTUP
        // ═══════════════════════════════════════════════════════════
        private void InitFonts()
        {
            // Resolved once, before anything asks: a shell or a document built later reads this
            // rather than re-scanning the installed families every time it needs a fallback.
            DefaultMonoFont = IsInstalled(PreferredMonoFont) ? PreferredMonoFont : string.Empty;

            ApplyAppFont(Services.ThemeManager.GetSetting(SetFontApp) ?? string.Empty);
            TerminalFontFamily = Services.ThemeManager.GetSetting(SetFontTerm) ?? string.Empty;

            // InvariantCulture both ways: a size written on a comma-decimal machine has to
            // still parse if the settings file is carried to a period-decimal one.
            TerminalFontSize = double.TryParse(Services.ThemeManager.GetSetting(SetFontTermSize),
                                               NumberStyles.Float, CultureInfo.InvariantCulture,
                                               out double sz)
                             ? ClampTermSize(sz)
                             : TermSizeDefault;
        }

        /// <summary>True when <paramref name="family"/> is installed on this machine.</summary>
        /// <remarks>
        /// A name scan over the installed families, not the readability or fixed-width guards
        /// below: those resolve a typeface per family and are why the terminal list is built
        /// lazily on first open rather than at startup. This one runs once and reads a string.
        /// </remarks>
        private static bool IsInstalled(string family)
        {
            try
            {
                foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
                    if (string.Equals(f.Source, family, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        private static double ClampTermSize(double v) =>
            Math.Round(Math.Max(TermSizeMin, Math.Min(TermSizeMax, v)));

        /// <summary>
        /// Override the MonoFont resource, or remove the override to fall back to the shipped
        /// default. Removing rather than re-setting matters: the merged dictionaries still hold
        /// the original, so the fallback stays correct without this having to know what it was.
        /// </summary>
        private static void ApplyAppFont(string value)
        {
            var fam = ResolveFont(value);
            if (fam == null) Application.Current.Resources.Remove("MonoFont");
            else Application.Current.Resources["MonoFont"] = fam;
        }

        // Named ResolveFont, not Resolve: MainWindow is one partial class spread over fifty
        // files, so a bare verb here collides with TerminalTabs.cs's folder Resolve.
        private static FontFamily? ResolveFont(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try { return new FontFamily(value); }
            catch { return null; }   // an uninstalled font: fall back rather than throw
        }

        // ═══════════════════════════════════════════════════════════
        //  READABILITY GUARD
        // ═══════════════════════════════════════════════════════════
        private const string GuardChars =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        /// <summary>
        /// A usable text font maps plain letters and digits to real glyphs. Symbol families
        /// (Wingdings, Webdings, Marlett) map none of them, and picking one turns the whole UI
        /// into dingbats with no obvious way back.
        /// </summary>
        private static bool IsReadable(FontFamily fam)
        {
            try
            {
                foreach (var tf in fam.GetTypefaces())
                    if (tf.TryGetGlyphTypeface(out var g)
                        && GuardChars.All(c => g.CharacterToGlyphMap.ContainsKey(c)))
                        return true;
            }
            catch { }
            return false;   // composite-only or broken: nothing we could verify
        }

        // A narrow letter, two wide ones, a digit and a space. If a face gives all five the same
        // advance it is fixed width; a proportional face fails on the first pair.
        private static readonly char[] WidthProbe = { 'i', 'M', 'W', '0', ' ' };

        /// <summary>
        /// True for a fixed-width family. The terminal list is filtered by this because the
        /// renderer lays the grid out from ONE advance width (TerminalControl.LoadFont), so a
        /// proportional face does not merely look wrong, it shears every column out of line.
        /// </summary>
        /// <remarks>
        /// Any weight of a monospaced family is monospaced, so the first typeface that resolves
        /// answers for the family and there is no need to walk the rest.
        /// </remarks>
        private static bool IsMonospaced(FontFamily fam)
        {
            try
            {
                foreach (var tf in fam.GetTypefaces())
                {
                    if (!tf.TryGetGlyphTypeface(out var g)) continue;

                    double first = 0;
                    bool have = false;
                    foreach (var c in WidthProbe)
                    {
                        if (!g.CharacterToGlyphMap.TryGetValue(c, out ushort gi)) return false;
                        double w = g.AdvanceWidths[gi];
                        if (!have) { first = w; have = true; }
                        else if (Math.Abs(w - first) > 0.0001) return false;
                    }
                    return have;
                }
            }
            catch { }
            return false;
        }

        // ═══════════════════════════════════════════════════════════
        //  DIALOG
        // ═══════════════════════════════════════════════════════════
        private void FontsRow_Click(object sender, RoutedEventArgs e)
        {
            ThemePopup.IsOpen = false;
            if (!_fontsBuilt) BuildFontCombos();
            SyncFontCombos();
            FontsOverlay.Visibility = Visibility.Visible;
            Anim.FadeIn(FontsOverlay);       // Anim.cs
        }

        private void FontsClose_Click(object sender, RoutedEventArgs e) => HideFonts();
        private void FontsOverlay_Click(object sender, MouseButtonEventArgs e) => HideFonts();
        private void FontsCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void HideFonts() => FontsOverlay.Visibility = Visibility.Collapsed;

        /// <summary>
        /// Default row plus the installed families each slot can actually use.
        /// </summary>
        /// <remarks>
        /// The app list is unfiltered: guarding all of them up front costs seconds on a machine
        /// with a big font set, so the readability guard runs on selection instead, where it
        /// only has to judge one. The TERMINAL list is filtered to fixed-width faces, because
        /// there the wrong pick does not just look bad, it shears the grid - and a guard that
        /// fires after the fact would mean offering a choice that silently does not take. That
        /// filter is the one place the per-family cost is worth paying, and it is paid once, on
        /// the first open of this dialog, not at startup.
        /// </remarks>
        private void BuildFontCombos()
        {
            _fontsBuilt = true;

            var families = System.Windows.Media.Fonts.SystemFontFamilies
                .OrderBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
                .ToList();

            static FontChoice Choice(FontFamily f) =>
                new() { Display = f.Source, Value = f.Source, Fam = f };

            var all  = families.Select(Choice).ToList();
            var mono = families.Where(IsMonospaced).Select(Choice).ToList();

            // Each combo gets its OWN list, headed by its own default row naming what that slot
            // actually falls back to. Sharing one list would share selection state.
            List<FontChoice> WithDefault(List<FontChoice> from, string shipped)
            {
                var l = new List<FontChoice>(from.Count + 1)
                {
                    new() { Display = Loc("Str_Fonts_Default") + " (" + shipped + ")" },
                };
                l.AddRange(from);
                return l;
            }

            _fontsSyncing = true;
            // Each default row names what that slot ACTUALLY falls back to, which for the two
            // monospaced slots is the preferred face when this machine has it and the old chain
            // when it does not - so the row never promises a font that is not there.
            string monoDefault = DefaultMonoFont;

            FontAppCombo.ItemsSource    = WithDefault(all,  ShippedAppFont());
            FontTermCombo.ItemsSource   = WithDefault(mono, monoDefault.Length > 0 ? monoDefault : "Cascadia Mono");
            FontEditorCombo.ItemsSource = WithDefault(mono, monoDefault.Length > 0 ? monoDefault : ShippedAppFont());
            _fontsSyncing = false;
        }

        /// <summary>
        /// MonoFont as AppStyles.xaml shipped it. The user's override lives in the top-level
        /// application dictionary, so the merged ones still hold the original - which keeps this
        /// right even while an override is active.
        /// </summary>
        private static string ShippedAppFont()
        {
            foreach (var d in Application.Current.Resources.MergedDictionaries)
                if (d.Contains("MonoFont") && d["MonoFont"] is FontFamily f)
                    return f.Source;
            return "Consolas";
        }

        private void SyncFontCombos()
        {
            _fontsSyncing = true;
            Select(FontAppCombo,    Services.ThemeManager.GetSetting(SetFontApp) ?? string.Empty);
            Select(FontTermCombo,   TerminalFontFamily);
            Select(FontEditorCombo, Editing.EditorOptions.FontFamily);

            FontTermSize.Value   = TerminalFontSize;
            FontEditorSize.Value = Editing.EditorOptions.FontSize;
            ShowTermSize();
            ShowEditorSize();
            _fontsSyncing = false;
        }

        private void ShowTermSize() =>
            FontTermSizeLabel.Text = TerminalFontSize.ToString("0", CultureInfo.InvariantCulture);

        private void ShowEditorSize() =>
            FontEditorSizeLabel.Text = Editing.EditorOptions.FontSize.ToString("0", CultureInfo.InvariantCulture);

        /// <summary>
        /// Editor point size. Pushed to every open document as well as saved, the same way the
        /// terminal slider behaves - and it is the same value Ctrl+wheel over a document moves,
        /// so the two can never disagree (Editing/EditorOptions.cs).
        /// </summary>
        private void FontEditorSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Fires once during InitializeComponent, when Minimum="8" coerces the value up off
            // zero - before the readout beside it exists and before the settings have been read.
            // Same guard, and the same reason, as the terminal slider above.
            if (!_fontsBuilt) return;

            Editing.EditorOptions.FontSize = Editing.EditorOptions.ClampFont(e.NewValue);
            ShowEditorSize();
            if (_fontsSyncing) return;   // the dialog opening is not a user edit

            ApplyEditorOptions();        // EditorBar.cs - saves and repaints every open document
        }

        /// <summary>
        /// Terminal point size. Pushed to every OPEN shell as well as saved for the next one,
        /// so dragging the slider reads as a live setting rather than as something that only
        /// shows up in the next tab you open.
        /// </summary>
        private void FontTermSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // This fires once during InitializeComponent, when Minimum="8" coerces the value up
            // off zero - before the readout beside it has been assigned and before InitFonts has
            // read the saved size. Acting on that would null-reference, then persist 8 over
            // whatever the user actually had. Nothing is live until the dialog has been built.
            if (!_fontsBuilt) return;

            TerminalFontSize = ClampTermSize(e.NewValue);
            ShowTermSize();
            if (_fontsSyncing) return;   // the dialog opening is not a user edit

            Services.ThemeManager.SetSetting(SetFontTermSize,
                TerminalFontSize.ToString("0", CultureInfo.InvariantCulture));

            foreach (var p in new[] { LeftPane, RightPane })
                foreach (var t in p.Tabs)
                    t.Term?.SetFontSize(TerminalFontSize);
        }

        private static void Select(ComboBox cb, string value)
        {
            if (cb.ItemsSource is not IEnumerable<FontChoice> items) return;
            cb.SelectedItem = items.FirstOrDefault(f => f.Value == value)
                           ?? items.FirstOrDefault(f => f.Value.Length == 0);
        }

        private void FontCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_fontsSyncing || sender is not ComboBox cb || cb.SelectedItem is not FontChoice c) return;

            // The default row is always allowed through; a real family has to pass the guard.
            if (c.Value.Length > 0 && c.Fam != null && !IsReadable(c.Fam))
            {
                if (_active != null) SetTabStatusKey(_active, "Str_Fonts_Unreadable", c.Display);
                _fontsSyncing = true;
                Select(cb, ReferenceEquals(cb, FontAppCombo)
                        ? Services.ThemeManager.GetSetting(SetFontApp) ?? string.Empty
                     : ReferenceEquals(cb, FontEditorCombo)
                        ? Editing.EditorOptions.FontFamily
                        : TerminalFontFamily);
                _fontsSyncing = false;
                return;
            }

            if (ReferenceEquals(cb, FontAppCombo))
            {
                Services.ThemeManager.SetSetting(SetFontApp, c.Value);
                ApplyAppFont(c.Value);
            }
            else if (ReferenceEquals(cb, FontEditorCombo))
            {
                // Saved and applied through EditorOptions rather than written here: that class
                // is what pushes a font onto an editor, and two writers for one setting is how
                // the dialog and the document end up disagreeing.
                Editing.EditorOptions.FontFamily = c.Value;
                ApplyEditorOptions();     // EditorBar.cs
            }
            else
            {
                Services.ThemeManager.SetSetting(SetFontTerm, c.Value);
                TerminalFontFamily = c.Value;

                // Live, like the app slot: every open shell re-resolves its typeface and
                // repaints, rather than the choice only taking effect on the next tab.
                foreach (var p in new[] { LeftPane, RightPane })
                    foreach (var t in p.Tabs)
                        t.Term?.ReloadFont();
            }
        }

        /// <summary>
        /// The wheel browses fonts with live apply, which is the point of the dialog - you see
        /// the UI in the font rather than reading its name. Over an OPEN dropdown it scrolls the
        /// list instead.
        /// </summary>
        private void FontCombo_Wheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ComboBox cb || cb.IsDropDownOpen) return;
            if (cb.Items.Count == 0) return;

            e.Handled = true;
            int next = cb.SelectedIndex + (e.Delta > 0 ? -1 : 1);
            cb.SelectedIndex = Math.Max(0, Math.Min(cb.Items.Count - 1, next));
        }
    }
}
