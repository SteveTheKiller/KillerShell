using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace KillerShell.Services
{
    // Thirteen palettes, in picker order. SE98 cannot be written "98SE" because an enum member
    // may not start with a digit; ThemeFileName below maps it back to the real file and accent
    // folder name. Order matches KillerNotes, which is the reference for the theme set.
    public enum Theme
    {
        Dark, Light, Black, SE98, Blood, Greed, Cyanotic, Ectoplasm, Decay,
        Mourning, Sepulchre, Delirium, Malaise
    }
    public enum Accent { Green, Red, Blue, Purple, Orange, Teal }

    /// <summary>
    /// KillerUI / Grunge theme engine. Swaps the palette dictionary (MergedDictionaries[0]) in
    /// place at runtime; control styles bind brushes via DynamicResource so an in-place per-key
    /// update repaints everything. Persistence is pluggable (wire GetSetting/SetSetting at startup).
    /// Requires Themes/{Theme}.xaml at [0]. Shared trademark tokens are overlaid from
    /// the linked KillerUI contract; local dictionaries hold product-specific resources.
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>
        /// Read a corner radius out of the live theme. For code-built controls, which cannot use a
        /// DynamicResource and so had their radii as C# literals - the exact bug that kept the
        /// panes, tabs and status bar rounded on 98SE however many times the markup was zeroed.
        /// Accepts either a CornerRadius token or a bare Double, and falls back to the literal the
        /// caller used to carry so no theme moves if a key is missing.
        /// </summary>
        public static double Radius(string key, double fallback)
        {
            var v = System.Windows.Application.Current?.TryFindResource(key);
            if (v is System.Windows.CornerRadius cr) return cr.TopLeft;
            if (v is double d) return d;
            return fallback;
        }

        public static Func<string, string?> GetSetting { get; set; } = _ => null;
        public static Action<string, string> SetSetting { get; set; } = (_, _) => { };

        private static Theme _current = Theme.Dark;   // KillerShell default: Dark (Blue accent)
        private static Accent _darkAccent  = Accent.Blue;
        private static Accent _lightAccent = Accent.Green;
        private static Accent _blackAccent = Accent.Orange;
        private static Accent _se98Accent  = Accent.Green;

        public static Theme Current => _current;
        public static Accent AccentChoiceFor(Theme t) => AccentFor(t);

        private static Accent AccentFor(Theme t) =>
            t == Theme.Light ? _lightAccent
            : t == Theme.Black ? _blackAccent
            : t == Theme.SE98 ? _se98Accent
            : _darkAccent;

        private static bool HasAccents(Theme t) =>
            t == Theme.Dark || t == Theme.Light || t == Theme.Black || t == Theme.SE98;

        /// <summary>
        /// A foreground that is actually legible on <paramref name="fill"/>. Keeps the theme's
        /// own <paramref name="preferred"/> when that already clears WCAG AA (4.5:1), and
        /// otherwise falls back to whichever pole - near-black or white - scores higher.
        /// </summary>
        private static SolidColorBrush ReadableOn(SolidColorBrush? fill, SolidColorBrush? preferred)
        {
            var white = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            if (fill == null) return preferred ?? white;
            if (preferred != null && Contrast(fill.Color, preferred.Color) >= 4.5) return preferred;

            var ink = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
            var pick = Contrast(fill.Color, ink.Color) >= Contrast(fill.Color, white.Color) ? ink : white;
            pick.Freeze();
            return pick;
        }

        /// <summary>WCAG 2.1 contrast ratio between two opaque colors, 1.0 to 21.0.</summary>
        private static double Contrast(Color a, Color b)
        {
            double la = Relative(a), lb = Relative(b);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        private static double Relative(Color c)
        {
            static double Ch(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        /// <summary>File stem for a theme, and the name of its accent folder. Only SE98 differs,
        /// because an enum member cannot start with a digit but both the palette file and the
        /// accent folder are named "98SE".</summary>
        private static string ThemeFileName(Theme theme) =>
            theme == Theme.SE98 ? "98SE" : theme.ToString();

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
            // "98SE" is what the setting has always been written as by the other apps in the
            // family, and it is what the file is called - but it does not parse as the enum
            // member, so it is mapped by hand before TryParse gets a look at it.
            string? saved = GetSetting(ThemeKey);
            _current = saved == "98SE" ? Theme.SE98
                     : Enum.TryParse<Theme>(saved, out var t) ? t
                     : (Elevated ? Theme.Blood : _current);

            _darkAccent  = Enum.TryParse<Accent>(GetSetting("DarkAccent"),  out var da) ? da : _darkAccent;
            _lightAccent = Enum.TryParse<Accent>(GetSetting("LightAccent"), out var la) ? la : _lightAccent;
            _blackAccent = Enum.TryParse<Accent>(GetSetting("BlackAccent"), out var ba) ? ba : _blackAccent;
            _se98Accent  = Enum.TryParse<Accent>(GetSetting("98SEAccent"),  out var wa) ? wa : _se98Accent;
            LoadDict(_current);
        }

        public static void Apply(Theme theme)
        {
            _current = theme;
            // Persist the FILE name, not the enum name, so the stored value reads "98SE" the
            // same way every other app in the family writes it.
            SetSetting(ThemeKey, ThemeFileName(theme));
            LoadDict(theme);
            ThemeChanged?.Invoke();
        }

        public static void ApplyAccent(Theme family, Accent accent)
        {
            if      (family == Theme.Light) { _lightAccent = accent; SetSetting("LightAccent", accent.ToString()); }
            else if (family == Theme.Black) { _blackAccent = accent; SetSetting("BlackAccent", accent.ToString()); }
            else if (family == Theme.SE98)  { _se98Accent  = accent; SetSetting("98SEAccent",  accent.ToString()); }
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
            // that was 150+ passes over the whole visual tree on every single theme click - a
            // huge lag on theme switch, nowhere near KillerPDF's instant swap. Confirmed as
            // reproducing even with zero terminal/editor tabs open, which ruled out
            // RefreshTerminalThemes/RefreshEditorThemes and pointed straight at this loop.
            // ThemeFileName, never theme.ToString(): the 98SE palette, its KillerUI half and its
            // accent folder are all named for the digits, which the enum member cannot be.
            string name = ThemeFileName(theme);

            var combined = new ResourceDictionary();
            var newDict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{name}.xaml") };
            foreach (object key in newDict.Keys)
                combined[key] = newDict[key];
            KillerThemeContract.Apply(combined, name);

            var accent = AccentFor(theme);
            if (HasAccents(theme) && accent != Accent.Green)
            {
                string family = theme == Theme.Light ? "Light"
                              : theme == Theme.Black ? "Black"
                              : theme == Theme.SE98  ? "98SE"
                              : "Dark";
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


            // ── 98SE chrome contract ────────────────────────────────────────────────────
            //
            // 98SE is the only FLAT theme: a hard 2px frame, raised bevels, a short Win98
            // caption. It carries 126 keys no other palette declares, and controls that bind
            // them have to resolve on all thirteen themes - so every one gets a neutral default
            // here and 98SE's own value simply wins, with no branch on the theme name anywhere.
            //
            // SetIfAbsent, never a plain assignment: the palette is read FIRST and must not be
            // overwritten. Mirror() points a token at an existing brush so a default tracks the
            // theme instead of being a dead literal.
            {
                var Transparent = new SolidColorBrush(Colors.Transparent);
                Transparent.Freeze();

                void SetIfAbsent(string key, object value)
                {
                    if (!combined.Contains(key)) combined[key] = value;
                }
                void Mirror(string key, string source)
                {
                    if (!combined.Contains(key) && combined.Contains(source))
                        combined[key] = combined[source];
                    else if (!combined.Contains(key)) combined[key] = Transparent;
                }

            SetIfAbsent("AboutCaptionMargin", new Thickness(0));
            Mirror("AboutPanelBrush", "PaneBrush");
            // TRANSPARENT with zero thickness: no ordinary theme draws a lifted edge on its bars.
            // Mirroring PaneBorderBrush would have been harmless only because the thickness is 0,
            // which is the kind of accident that breaks the moment someone changes the thickness.
            SetIfAbsent("BarEdgeBrush", Transparent);
            SetIfAbsent("BarEdgeThickness", new Thickness(0));
            // The bar's DARK half, so a raised toolbar has both edges. Transparent/0 by default,
            // exactly like BarEdge* above, so no other theme grows one.
            SetIfAbsent("BarEdgeDarkBrush", Transparent);
            SetIfAbsent("BarEdgeDarkThickness", new Thickness(0));
            SetIfAbsent("BarPadding", new Thickness(0));
            Mirror("BarSepBrush", "PaneBorderBrush");
            SetIfAbsent("BarSepWidth", 0.0);
            SetIfAbsent("BarShadowOpacity", 0.38);
            SetIfAbsent("BevelDarkBrush", Transparent);
            SetIfAbsent("BevelDarkThickness", new Thickness(0));
            SetIfAbsent("BevelLightBrush", Transparent);
            SetIfAbsent("BevelLightThickness", new Thickness(0));
            // The pane's outer radius and the nested-bar radius, as SCALARS. Tabs.cs builds its
            // corners one at a time from first/last-tab state, so it needs the number rather than
            // a ready-made CornerRadius - and it had the numbers as literals, which is why the
            // panes kept three rounded corners on 98SE no matter what the markup said.
            SetIfAbsent("PaneCornerRadiusValue", 6.0);
            SetIfAbsent("BarCornerRadiusValue", 5.0);
            // The pane card's outer inset and its 1px ring, and the tab strip's inset. Defaults are
            // exactly what FilePane.xaml had hardcoded, so the twelve rounded themes are untouched.
            SetIfAbsent("PaneOuterMargin", new Thickness(0, -1, 8, 0));
            SetIfAbsent("TabBarMargin", new Thickness(0, 6, 8, 0));
            SetIfAbsent("PaneEdgeThickness", new Thickness(1));
            // The ACTIVE tab's dark bevel: BevelDarkThickness with the BOTTOM dropped, so the tab
            // opens into the content below it instead of ruling a line across the join. 0 here
            // rather than a computed "BevelDarkThickness minus its bottom" - on the twelve
            // non-flat themes the bevel is zero already, so the two are the same value, and a
            // literal cannot go wrong if a theme later states an asymmetric bevel.
            SetIfAbsent("TabActiveBevelDarkThickness", new Thickness(0));
            // The INACTIVE tab's dark bevel. Mirrors BevelDarkThickness's default of 0, so the
            // twelve rounded themes are unaffected; a flat theme states a 1px foot instead of the
            // shared 2px, because a doubled rule under the tab strip reads as a sunken menu bar.
            SetIfAbsent("TabInactiveBevelDarkThickness", new Thickness(0));
            // The tab band's ring line when the pane is NOT the lit half of a dual pane - which in
            // a single-pane window is always. UpdatePaneFocusRing (DualPane.cs) used to hardcode
            // PaneBorderBrush here, overriding the transparent PaneEdgeBrush the markup asks for,
            // so on 98SE the ring drew a gray rule at the foot of the tab strip AND two 1px
            // verticals hanging 6px into the top of the pane - the line under the active tab and
            // the little gray line at the left of the menu bar were this one
            // element. Mirrors PaneBorderBrush by default, so the twelve rounded themes keep
            // exactly the brush they had; 98SE states it transparent.
            Mirror("TabRingIdleBrush", "PaneBorderBrush");
            // The 1px rule along the bottom of the shell/document bar (FilePane.xaml), which was a
            // hardcoded literal in the markup. Default is that literal, so nothing else moves.
            SetIfAbsent("BarUnderlineThickness", new Thickness(0, 0, 0, 1));
            // The footer row's height, applied by Chrome.cs. 24 is what MainWindow.xaml hardcoded,
            // so the twelve rounded themes are unchanged; a beveled theme states a taller one
            // because its sunken cells paint over the content instead of reserving space.
            SetIfAbsent("FooterHeight", 24.0);
            // The tab bevel's negative margin, which undoes the tab's padding so the bevel sits on
            // the tab's real edges. Default is the literal FilePane.xaml carried; a flat theme
            // trims the BOTTOM so the right-hand dark edge stops at the tab's foot instead of
            // hanging a pixel into the pane below it.
            SetIfAbsent("TabBevelMargin", new Thickness(-12, -4, -5, -5));
            // The folder tree's inset inside its well (MainWindow.xaml TreeFadeHost). Default is
            // the literal it replaced; a flat theme zeroes the top so the well's own fill does not
            // show above the first row as a white strip, and the scrollbar starts at the frame.
            SetIfAbsent("TreePanePadding", new Thickness(4, 6, 2, 6));
            Mirror("BgFlyout", "MenuBackgroundBrush");
            Mirror("ButtonEdgeBrush", "CardBorderBrush");
            SetIfAbsent("CaptionButtonBrush", Transparent);
            SetIfAbsent("CaptionButtonHeight", 36.0);
            SetIfAbsent("CaptionButtonMargin", new Thickness(0));
            SetIfAbsent("CaptionButtonWidth", 44.0);
            SetIfAbsent("CaptionButtonsMargin", new Thickness(0));
            Mirror("CaptionCloseBrush", "ChromeTextBrush");
            SetIfAbsent("CaptionCloseGap", new Thickness(0));
            // The close button's EXISTING hover: a red fill with white on it. Defaulting these to
            // transparent would have removed the red hover from all twelve non-flat themes the
            // moment ChromeCloseButton started binding them. 98SE states its own - a transparent
            // hover and a black glyph, because a Win98 close button reacted only to being pressed.
            // #e04444 is DangerRed's literal from Controls.xaml. It cannot be read from the
            // palette - DangerRed is a control-level resource, not a theme key, so
            // combined.Contains would always be false and the default would silently fall to
            // transparent, removing the red hover everywhere.
            if (!combined.Contains("CaptionCloseHoverBrush"))
            {
                var red = new SolidColorBrush(Color.FromRgb(0xE0, 0x44, 0x44)); red.Freeze();
                combined["CaptionCloseHoverBrush"] = red;
            }
            if (!combined.Contains("CaptionCloseHoverFgBrush"))
            {
                var white = new SolidColorBrush(Colors.White); white.Freeze();
                combined["CaptionCloseHoverFgBrush"] = white;
            }
            // TextBrush, not ChromeTextBrush: ChromeButton drew its glyphs in TextBrush before the
            // caption tokens existed, and ChromeTextBrush is a good deal dimmer (#b6b6b6 against
            // #e0e0e0 on Dark) - mirroring that would have quietly faded the window buttons on all
            // twelve non-flat themes. 98SE states its own, so it is unaffected either way.
            Mirror("CaptionGlyphBrush", "TextBrush");
            // PaneBorderBrush, matching what SurfaceButton actually drew. CardBorderBrush is a
            // different tier and is #787878 on Black, which would have put a bright gray box
            // round every secondary button.
            Mirror("ChipEdgeBrush", "PaneBorderBrush");
            // SurfaceButton's EXISTING face and hover - PaneBrush filling, RowHoverBrush on hover.
            // ChipFaceBrush had been mirroring SurfaceBrush, which is a different tier and would
            // have restyled every secondary button in the app.
            Mirror("ChipFaceBrush", "PaneBrush");
            Mirror("ChipHoverBrush", "RowHoverBrush");
            SetIfAbsent("ChromeFontFamily", new FontFamily("Segoe UI"));
            SetIfAbsent("ContentPaneMargin", new Thickness(0));
            SetIfAbsent("DialogButtonsMargin", new Thickness(0));
            SetIfAbsent("DialogContentMargin", new Thickness(0));
            SetIfAbsent("DialogFieldMargin", new Thickness(0));
            // 10, the halo every dialog already reserved for its drop shadow. 98SE sets 0: it
            // casts no shadow, and an invisible 10px gutter would put the resize grab outside
            // the visible window.
            SetIfAbsent("DialogHaloMargin", new Thickness(10));
            SetIfAbsent("DialogInlineSelVisibility", Visibility.Collapsed);
            SetIfAbsent("DialogRowMargin", new Thickness(0));
            SetIfAbsent("DialogStatusBarHeight", 0.0);
            SetIfAbsent("DialogStatusBarVisibility", Visibility.Collapsed);
            // 36, the caption row height every dialog already used. 98SE states its own short
            // band. Defaulting to KillerNotes' 28 would have shortened every dialog's title bar.
            //
            // This stays a DOUBLE: 98SE declares it as <sys:Double> and AboutCaptionHeight below
            // derives from it numerically, and both are assigned to FrameworkElement.Height, which
            // is a double.
            SetIfAbsent("DialogTitleBarHeight", 36.0);

            // The SAME height as a GridLength, for the one consumer that is a RowDefinition.
            //
            // A DynamicResource is assigned to its target property with NO type conversion - unlike
            // a literal XAML attribute, nothing runs a TypeConverter on the way in. So a double
            // reaching RowDefinition.Height, which is a GridLength, throws InvalidOperationException
            // out of DependencyObject.EvaluateExpression and takes the process down as an unhandled
            // XamlParseException the moment the dialog is constructed.
            //
            // That is exactly what happened: ConfirmDialog.xaml bound its caption row to
            // DialogTitleBarHeight, so clicking Install (or any other confirm) killed the app on
            // every theme, and it read as "the installer just closes the app" (2026-08-10).
            // Derived here rather than declared per theme so 98SE's own 20 is picked up too, and
            // so no palette file has to carry the same number twice in two types.
            combined["DialogTitleBarRowHeight"] = new GridLength(
                combined["DialogTitleBarHeight"] is double dialogCaptionH ? dialogCaptionH : 36.0);
            SetIfAbsent("EdgeFadeOpacity", 1.0);
            // The EDITOR's selection strength (Editing/EditorControl.cs), distinct from
            // TextSelectionOpacity which is the plain TextBox one. 98SE states 0.75 with
            // EditorSelectionOverlay true - a solid Win98 navy block with white text - where
            // every other theme keeps the translucent accent wash at 1.0.
            SetIfAbsent("EditorSelectionOpacity", 1.0);
            SetIfAbsent("EditorSelectionOverlay", false);
            // 1.0, because this scales the shadow BORDER's opacity and the effect already carries
            // its own 0.5 - the twelve non-flat themes must keep exactly the shadow they had.
            // 98SE states 0, which removes it entirely; a flat theme casts nothing.
            SetIfAbsent("FlyoutShadowOpacity", 1.0);
            Mirror("FooterBevelDarkBrush", "PaneBorderBrush");
            Mirror("FooterBevelLightBrush", "PaneBorderBrush");
            SetIfAbsent("FooterCellDarkThickness", new Thickness(0));
            SetIfAbsent("FooterCellLightThickness", new Thickness(0));
            SetIfAbsent("FooterCellMargin", new Thickness(0));
            SetIfAbsent("FooterCellPadding", new Thickness(0));
            SetIfAbsent("FooterPadding", new Thickness(0));
            SetIfAbsent("FrameInnerDarkBrush", Transparent);
            SetIfAbsent("FrameInnerDarkThickness", new Thickness(0));
            SetIfAbsent("FrameInnerLightBrush", Transparent);
            SetIfAbsent("FrameInnerLightThickness", new Thickness(0));
            SetIfAbsent("FrameInnerMargin", new Thickness(0));
            SetIfAbsent("FrameOuterDarkBrush", Transparent);
            SetIfAbsent("FrameOuterDarkThickness", new Thickness(0));
            SetIfAbsent("FrameOuterLightBrush", Transparent);
            SetIfAbsent("FrameOuterLightThickness", new Thickness(0));
            SetIfAbsent("GrainOpacity", 0.24);
            SetIfAbsent("IconShadowOpacity", 0.9);
            Mirror("InputEdgeBrush", "InputBorderBrush");
            // The surface a LIST sits on - the file listing and the terminal screen. Defaults to
            // the menu tier, which is where that content landed on 2026-08-08 (tab surface is
            // PaneBrush, content is MenuBackgroundBrush), so the other twelve themes are
            // unchanged. 98SE states #ffffff: a Win98 list is a sunken WHITE well, and on that
            // theme the menu tier is #c0c0c0, which came out as a gray content area.
            Mirror("ListPaneBrush", "MenuBackgroundBrush");

            // Two KillerShell-only surfaces a RETRO theme wants BLACK even when the rest of its
            // palette is light gray: the terminal screen and the Performance tab's metric tiles.
            // Both default to the surface they already used, so the other twelve are untouched;
            // 98SE states #000000 for each in its app layer (Themes/98SE.xaml).
            Mirror("TerminalBackgroundBrush", "ListPaneBrush");
            // Text ON a MonitorCellBrush surface (the Performance tab's tiles, info panel and
            // detail card). Plain text brushes everywhere - except a theme whose cells are a
            // different world from its page, like 98SE's black CRT readouts on a light gray app,
            // which states retro phosphor greens.
            Mirror("MonitorTextBrush", "TextBrush");
            Mirror("MonitorMutedBrush", "MutedTextBrush");
            // The DataGrids' alternate-row stripe (Events, Processes, Registry). RowAltBrush
            // everywhere, exactly as the style hardcoded; 98SE states a real Win98-adjacent
            // stripe - white rows with a gray that is NOT the window face gray.
            Mirror("GridRowAltBrush", "RowAltBrush");
            // The Performance tab's hover fill: RowHoverBrush everywhere - but on 98SE that is
            // the window face gray, which made a hovered black tile vanish into the window.
            Mirror("MonitorHoverBrush", "RowHoverBrush");
            // The tool tabs' content-well face (Events/Processes grid area, Registry values):
            // Transparent everywhere, so nothing changes on the ordinary themes; 98SE states
            // WHITE - a sunken client area.
            SetIfAbsent("ToolContentBrush", Transparent);
            // The Registry tree's fill. Transparent, so the tree shows the control's own
            // PaneBrush root (and its grain) and matches the value grid beside it, whose
            // ToolContentBrush face is Transparent the same way. 98SE states WHITE for both in
            // its own layer and is untouched.
            // This mirrored BackgroundBrush, which is a full-window LinearGradientBrush on five
            // themes - and a gradient painted into a narrow column re-ramps its ENTIRE sweep
            // inside that column, so the tree rendered the ramp's near end instead of the color
            // actually behind it: pink against Delirium's purple. Same recurring bug as the
            // picker side columns, the text fields and the tree chevron (BACKLOG.md).
            SetIfAbsent("ToolTreeBrush", Transparent);
            // The details pane's big filename: the family wordmark face everywhere, Courier New
            // on 98SE. NOT Mirror(): WordmarkFont lives in App.xaml, not in the theme
            // dictionaries, so Mirror's missing-source fallback handed every ordinary theme a
            // TRANSPARENT BRUSH as a font - "'#00FFFFFF' is not a valid value for property
            // 'FontFamily'", the Mourning theme-switch crash.
            if (!combined.Contains("DetailsNameFont"))
                combined["DetailsNameFont"] =
                    Application.Current?.TryFindResource("WordmarkFont") as System.Windows.Media.FontFamily
                    ?? new System.Windows.Media.FontFamily("Consolas");
            // The About card's content inset - 0 like the old AboutCaptionMargin the wrapper
            // borrowed; 98SE adds top room under the caption band.
            SetIfAbsent("AboutContentMargin", new Thickness(0));
            // The Performance tab's outer margins and per-tile margin - the literals they
            // replaced. 98SE collapses them to thin 2px seams so the black cells sit together
            // instead of floating in gray gutters.
            SetIfAbsent("MonitorDetailMargin",   new Thickness(4, 0, 8, 8));
            SetIfAbsent("MonitorInfoMargin",     new Thickness(8, 8, 8, 8));
            SetIfAbsent("MonitorTileListMargin", new Thickness(8, 0, 4, 8));
            SetIfAbsent("MonitorTileMargin",     new Thickness(6, 3, 6, 3));
            // The cell GRID's outer margin (the 2026-08-09 grid-of-cells layout). 2 a side so
            // the cells' own 6px MonitorTileMargin lands their edges at 8, flush with the info
            // panel above; 0 on 98SE so the cells run to the pane edge like every other well.
            SetIfAbsent("MonitorGridMargin", new Thickness(2, 0, 2, 5));
            // The tool-tab grids' margin (Events/Processes) and the Registry split's own: the
            // literals they replaced everywhere, 0 on 98SE so each sunken well is filled edge to
            // edge and runs flush to the pane.
            SetIfAbsent("ToolGridMargin", new Thickness(8, 0, 8, 8));
            SetIfAbsent("RegSplitMargin", new Thickness(8, 0, 8, 6));
            SetIfAbsent("RegGridMargin",  new Thickness(6, 0, 0, 0));
            SetIfAbsent("RegSplitterWidth", 5.0);
            // The dual-pane FOCUSED tab's border/padding sets - exactly the literals the four
            // PaneFocused triggers in FilePane.xaml hardcoded. 98SE zeroes the thicknesses and
            // keeps the active padding: its ring brush is transparent, and even a transparent
            // 1px border kept the tab's fill out of its own edge column, which let the menu
            // bar's white top line show through as a stray white pixel.
            SetIfAbsent("TabFocusThickness",      new Thickness(1, 3, 1, 0));
            SetIfAbsent("TabFocusFirstThickness", new Thickness(0, 3, 1, 0));
            SetIfAbsent("TabFocusLastThickness",  new Thickness(1, 3, 0, 0));
            SetIfAbsent("TabFocusOnlyThickness",  new Thickness(0, 3, 0, 0));
            SetIfAbsent("TabFocusPadding",      new Thickness(11, 1, 4, 5));
            SetIfAbsent("TabFocusFirstPadding", new Thickness(12, 1, 4, 5));
            SetIfAbsent("TabFocusLastPadding",  new Thickness(11, 1, 5, 5));
            SetIfAbsent("TabFocusOnlyPadding",  new Thickness(12, 1, 5, 5));
            Mirror("MonitorCellBrush", "MenuBackgroundBrush");

            // A terminal that overrides its BACKGROUND has to override its foreground and accent
            // too, or the text is picked for the wrong surface. 98SE is the case that proves it:
            // its TextBrush is #000000 and its PrimaryBrush #004f00, both chosen against a light
            // gray app - on the black console they are invisible and nearly invisible.
            // These default to the app's own, so the other twelve are unchanged.
            Mirror("TerminalForegroundBrush", "TextBrush");
            Mirror("TerminalAccentBrush", "PrimaryBrush");
            SetIfAbsent("MenuBevel2DarkBrush", Transparent);
            SetIfAbsent("MenuBevel2DarkThickness", new Thickness(0));
            SetIfAbsent("MenuBevel2LightBrush", Transparent);
            SetIfAbsent("MenuBevel2LightThickness", new Thickness(0));
            SetIfAbsent("MenuBevelDarkBrush", Transparent);
            SetIfAbsent("MenuBevelDarkThickness", new Thickness(0));
            SetIfAbsent("MenuBevelInnerMargin", new Thickness(0));
            SetIfAbsent("MenuBevelLightBrush", Transparent);
            SetIfAbsent("MenuBevelLightThickness", new Thickness(0));
            SetIfAbsent("MenuTextBrush", Transparent);
            // The outline button, split into the six parts a Win98 button needs. Defaults keep
            // exactly what OutlineButton already did: transparent at rest with an accent edge and
            // accent text, filling with the accent on hover under the AA-derived label color.
            // 98SE turns it into a real raised gray button - a #c0c0c0 face at rest, lighter on
            // hover, darker when pressed, black text throughout and NO accent anywhere.
            SetIfAbsent("OutlineFaceBrush", Transparent);          // rest FILL (transparent = outline)
            Mirror("OutlineRestBrush", "OutlineBtnBrush");         // rest EDGE
            Mirror("OutlineTextBrush", "OutlineBtnBrush");         // rest TEXT
            Mirror("OutlineHoverBrush", "OutlineBtnBrush");        // hover FILL
            // OutlineHoverTextBrush is set AFTER the accent overlay, next to OnOutlineBtnBrush -
            // it defaults to that value and it does not exist yet at this point in the method.
            Mirror("OutlinePressedBrush", "RowSelectedBrush");     // pressed FILL
            SetIfAbsent("PaneBevel2DarkThickness", new Thickness(0));
            SetIfAbsent("PaneBevel2LightThickness", new Thickness(0));
            SetIfAbsent("PaneBevelDark2Brush", Transparent);
            SetIfAbsent("PaneBevelDarkBrush", Transparent);
            SetIfAbsent("PaneBevelDarkThickness", new Thickness(0));
            SetIfAbsent("PaneBevelInnerMargin", new Thickness(0));
            SetIfAbsent("PaneBevelLight2Brush", Transparent);
            SetIfAbsent("PaneBevelLightBrush", Transparent);
            SetIfAbsent("PaneBevelLightThickness", new Thickness(0));
            Mirror("PaneEdgeBrush", "PaneBorderBrush");
            SetIfAbsent("PaneShadowOpacity", 0.6);
            // TRANSPARENT: a theme radio row has no fill of its own on any ordinary theme.
            // Mirroring SurfaceBrush would have put an opaque tile behind every option in the
            // theme and language flyouts.
            SetIfAbsent("RadioWellBrush", Transparent);
            SetIfAbsent("RowSelectedBrush", Transparent);
            SetIfAbsent("ScrollArrowSize", 0.0);
            // 12, the width the ScrollBar style already used. 98SE asks for 16, a real Win98 bar.
            SetIfAbsent("ScrollBarThickness", 12.0);
            SetIfAbsent("ScrollThumbBrush", Transparent);
            SetIfAbsent("ScrollThumbHoverBrush", Transparent);
            // 4,0 - the inset the thumb already had, which is what makes it read as a slim
            // overlay rather than filling the gutter. 98SE states 0: a Win98 thumb fills its
            // track edge to edge. The horizontal orientation trigger flips this to 0,4 and the
            // hover trigger to 2,2, both of which still override this as they always did.
            SetIfAbsent("ScrollThumbMargin", new Thickness(4, 0, 4, 0));
            // The HORIZONTAL thumb's inset - the 0,4 the template hardcoded in its orientation
            // trigger, which kept the slim-overlay squeeze on 98SE's 16px bar (the "too skinny"
            // tree scrollbar). 98SE states 0: a Win98 thumb fills its track.
            SetIfAbsent("ScrollThumbMarginH", new Thickness(0, 4, 0, 4));
            // 3, the radius the thumb already had. 98SE states 0 - a Win98 thumb is square.
            SetIfAbsent("ScrollThumbRadius", new CornerRadius(3));
            SetIfAbsent("ChartCornerRadius", new CornerRadius(4));
            // The 45-degree cut across a tab's top corners. 0 = no chamfer, and
            // TabChamferConverter returns a null Clip for 0, so the twelve rounded themes are not
            // clipped at all. Only a flat theme states a value.
            SetIfAbsent("TabChamfer", 0.0);
            // The tab's own padding. Default is the literal FilePane.xaml carried; a flat theme
            // trims a pixel off the bottom for the shorter Win98 tab.
            SetIfAbsent("TabPadding", new Thickness(12, 4, 5, 5));
            // No PageBevel* keys: the tab page reuses the PaneBevel* set the sidebar well already
            // uses, so the two recesses are the same four Borders and cannot drift apart. A
            // single-ring PageBevel* pair was tried on 2026-08-09 and rejected - a Win98 recess is
            // a DOUBLE bevel (outer #808080/#ffffff, inner #000000/#c0c0c0 at a 1px margin).
            // The details strip's own edge and its internal fields|preview divider. Defaults are
            // the literals FilePane.xaml carried (a top rule and a 1px rule); a flat theme drops
            // both so the strip is completely flat.
            SetIfAbsent("DetailsPaneBorderThickness", new Thickness(0, 1, 0, 0));
            SetIfAbsent("DetailsDividerWidth", 1.0);
            // The tab's outer margin. Default is the literal it replaced; a flat theme opens a 2px
            // gap on the right so both chamfered corners are visible against the band.
            SetIfAbsent("TabMargin", new Thickness(0, 3, 0, 1));
            // The ACTIVE tab's dark-bevel margin. Defaults to the same family literal the shared
            // TabBevelMargin uses (harmless - only a flat theme draws tab bevels at all); 98SE
            // pulls its bottom in so the dark right edge stops at the menu bar's white top line.
            SetIfAbsent("TabActiveBevelDarkMargin", new Thickness(-12, -4, -5, -5));
            SetIfAbsent("TabSeamPatchBrush", Transparent);
            // ComboBox chrome - all defaults are exactly what the template hardcoded, so the
            // ordinary themes render untouched; 98SE turns the field white, the drop arrow into
            // a raised gray Marlett-triangle button, and the list into a white well.
            // ComboFieldBrush is NOT set here - it mirrors TextFieldBrush, which is computed
            // further down, and a mirror reads its source at the line it runs on. See there.
            Mirror("ComboPopupBrush", "MenuBackgroundBrush");
            SetIfAbsent("ComboButtonBrush", Transparent);
            SetIfAbsent("ComboButtonMinWidth", 0.0);
            SetIfAbsent("ComboChevMargin", new Thickness(5, 0, 0, 0));
            SetIfAbsent("ComboChevGlyphMargin", new Thickness(0, 1, 0, 0));
            SetIfAbsent("ComboChevGlyph", "\uE70D");
            SetIfAbsent("ComboChevFont", new System.Windows.Media.FontFamily("Segoe MDL2 Assets"));
            // The footer STATUS cell's inner inset. Deliberately its own key rather than reusing
            // FooterCellPadding, which belongs to the version cell and carries a different number.
            // Default is the literal MainWindow.xaml had, so the twelve rounded themes do not move.
            SetIfAbsent("FooterStatusPadding", new Thickness(10.5, 0, 12, 0));
            SetIfAbsent("ScrollTrackBevelDark", Transparent);
            SetIfAbsent("ScrollTrackBevelLight", Transparent);
            // TRANSPARENT: the scrollbar is an overlay on every ordinary theme and has never had
            // a visible track. Mirroring BackgroundBrush would have painted a gradient strip down
            // the side of every list on the five gradient themes. 98SE states a real track.
            SetIfAbsent("ScrollTrackBrush", Transparent);
            // A text FIELD is a small recessed surface and must never carry the WINDOW's own
            // gradient: BackgroundBrush is a full-window LinearGradientBrush on five themes,
            // and a brush like that re-ramps its whole sweep inside every input box - which is
            // exactly what put a gradient in the Storage tab's target box (2026-08-09). Solid
            // themes keep exactly the color they always had (the gradient check simply fails
            // and the old mirror runs); gradient themes get a SOLID field in the gradient's
            // own starting color, so the fill still belongs to the palette. Computed HERE,
            // before SearchFieldBrush, which now mirrors it - the two always shared one value
            // and the address box had the same gradient. Derived-token order matters: a
            // mirror reads its source at this line, not later (the FlyoutCardEffect lesson).
            if (!combined.Contains("TextFieldBrush")
                && combined["BackgroundBrush"] is LinearGradientBrush bgGrad
                && bgGrad.GradientStops.Count > 0)
            {
                var solidField = new SolidColorBrush(bgGrad.GradientStops[0].Color);
                solidField.Freeze();
                combined["TextFieldBrush"] = solidField;
            }
            else Mirror("TextFieldBrush", "BackgroundBrush");
            Mirror("SearchFieldBrush", "TextFieldBrush");
            // A ComboBox closed face is a FIELD, so it takes the field brush - not
            // BackgroundBrush, which it mirrored until 2026-08-09. That put the full-window
            // gradient inside a 130px dropdown, where it re-ramped its entire sweep and painted
            // the box a color from the middle of the ramp: the Event Viewer's two pickers came
            // up as purple-to-magenta bars against the tab. Same bug as the text fields
            // directly above, and it has to be mirrored HERE, after TextFieldBrush exists.
            // Solid themes are unaffected - TextFieldBrush is BackgroundBrush there, the exact
            // value this key already had. 98SE states its own #ffffff and is untouched.
            Mirror("ComboFieldBrush", "TextFieldBrush");

            // A SOLID stand-in for the window background, for anything small that needs to read
            // as "the same color as what is behind me". BackgroundBrush itself cannot be used
            // there: it is a full-window LinearGradientBrush on five themes, and a gradient
            // painted into a 16px box re-ramps its ENTIRE sweep inside those 16 pixels, so the
            // shape comes out some arbitrary color from the middle of the ramp instead of
            // matching its surroundings. That is what made the tree's expanded chevron a gray
            // wedge instead of disappearing into the sidebar (2026-08-09), and it is the same
            // trap the picker panes and the text fields both hit.
            if (!combined.Contains("SolidBackgroundBrush"))
            {
                if (combined["BackgroundBrush"] is LinearGradientBrush wg && wg.GradientStops.Count > 0)
                {
                    var solid = new SolidColorBrush(wg.GradientStops[0].Color);
                    solid.Freeze();
                    combined["SolidBackgroundBrush"] = solid;
                }
                else Mirror("SolidBackgroundBrush", "BackgroundBrush");
            }
            // The fill behind a tree chevron, whose only job is to mask the connecting line, so
            // it must be whatever surface that particular tree sits on. SolidBackgroundBrush is
            // right for the FOLDER tree, which shows the window through it - but the Registry
            // Editor's tree sits on its control's PaneBrush, so the same fill painted a wedge of
            // window color there (pink on the gradient themes, whose first stop is nothing like
            // the pane). RegistryEditorControl overrides this key on its own TreeView's
            // Resources, which is where the template's DynamicResource lookup finds it first.
            Mirror("TreeChevronMaskBrush", "SolidBackgroundBrush");
            // TRANSPARENT, not PaneBrush: the sidebar well is a 98SE idea (a sunken white list
            // box). Every other theme lets the window show through the tree exactly as before, so
            // wiring this must not hand them an opaque fill they never had.
            SetIfAbsent("SidebarPaneBrush", Transparent);
            SetIfAbsent("SidebarPanelMargin", new Thickness(0));
            // The slider's unfilled groove - InputBorderBrush, which is what ThinSlider drew.
            // Transparent would have made the track vanish on every theme.
            Mirror("SliderTrack", "InputBorderBrush");
            // TRANSPARENT: a toolbar button has no face on any ordinary theme, it is a bare glyph
            // that washes on hover. Mirroring PaneBrush would have given every one of them an
            // opaque tile. 98SE states #c0c0c0 - there, a toolbar button IS a raised button.
            SetIfAbsent("SortButtonBrush", Transparent);
            Mirror("SurfaceHoverBrush", "RowHoverBrush");
            // The selection's EXISTING values, moved into the contract rather than replaced by
            // it: DarkTextBox drew a PrimaryBrush selection at 0.3. TextFieldBrush itself is
            // computed further up (before SearchFieldBrush mirrors it), where the gradient
            // themes get a solid field instead of the window sweep.
            Mirror("TextSelectionBrush", "PrimaryBrush");
            SetIfAbsent("TextSelectionOpacity", 0.3);
            // TextBrush, not SelectionFg. The existing selection is PrimaryBrush at 0.3 opacity -
            // a translucent wash the normal text shows through - so the text color must not
            // change or every selection in the app would repaint. 98SE states white, which it
            // needs because its selection is a solid navy fill at full opacity.
            Mirror("TextSelectionTextBrush", "TextBrush");
            SetIfAbsent("TitleBarBleed", new Thickness(0));
            SetIfAbsent("TitleBarHeight", 36.0);
            // These six are the title bar's EXISTING hardcoded numbers, moved into the contract
            // rather than replaced by it. A generic default would have quietly resized the icon
            // and the wordmark on all twelve non-flat themes the moment the markup started
            // binding them; 98SE states its own and is the only theme that changes.
            SetIfAbsent("TitleBarPadding", new Thickness(14, 0, 14, 0));
            SetIfAbsent("TitleIconMargin", new Thickness(0, 0, 7, 0));
            SetIfAbsent("TitleIconSize", 27.0);
            SetIfAbsent("TitleTextMargin", new Thickness(0));
            SetIfAbsent("TitleWordmarkBoldSize", 23.4);
            SetIfAbsent("TitleWordmarkSize", 18.0);
            SetIfAbsent("UseDialogCaption", false);
            Mirror("WindowEdgeBrush", "AppBorderBrush");
            SetIfAbsent("WindowEdgeThickness", new Thickness(0));
            // The frame's FACE. 98SE does not declare one, and a transparent default left a
            // see-through gutter around the window where its 5,4,5,5 padding
            // is. AppBorderBrush is the right source: it is already the frame's color,
            // #c0c0c0 on 98SE - the Win98 gray the bevels are cut into. Harmless on the other
            // twelve, whose WindowFramePadding is 0, so the Border has no visible area at all.
            // NOT BackgroundBrush, which is a full-window gradient on five themes and would have
            // re-ramped behind everything.
            Mirror("WindowFrameBrush", "AppBorderBrush");
            SetIfAbsent("WindowFrameMargin", new Thickness(0));
            SetIfAbsent("WindowFramePadding", new Thickness(0));
            SetIfAbsent("WindowFrameThickness", new Thickness(0));
            // Defaults to the theme's own icon-shadow strength, which is what the wordmark's
            // blurred copy already used. A flat theme states 0 - no emboss behind a Win98
            // caption. Copied rather than SetIfAbsent(0.0), which would have removed the emboss
            // from all twelve non-flat themes.
            if (!combined.Contains("WordmarkEmbossOpacity"))
                combined["WordmarkEmbossOpacity"] = combined.Contains("IconShadowOpacity")
                    ? combined["IconShadowOpacity"] : 0.9;

                // Derived, not defaulted: these follow from whether the theme asked for a flat
                // Win98 caption at all, so they cannot be a fixed literal.
                bool flat = combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is bool f && f;

                // A Win98 caption has no logotype in it - just the icon and the window name in
                // plain bold - so the wordmark and the plain title trade places.
                combined["WordmarkVisibility"]   = flat ? Visibility.Collapsed : Visibility.Visible;
                combined["PlainTitleVisibility"] = flat ? Visibility.Visible   : Visibility.Collapsed;

                // The resize grip. Win98 drew three beveled DIAGONAL lines in the corner, not the
                // dotted triangle the rest of the family uses. The two shapes
                // are different geometry, not a recolor, so both sit in the markup and trade
                // visibility rather than one being restyled into the other.
                combined["GripDotsVisibility"]  = flat ? Visibility.Collapsed : Visibility.Visible;
                combined["GripLinesVisibility"] = flat ? Visibility.Visible   : Visibility.Collapsed;

                // A Win98 caption button reacted only to being PRESSED, never to hover.
                if (!combined.Contains("CaptionHoverBrush"))
                    combined["CaptionHoverBrush"] = flat ? (object)Transparent : combined["RowHoverBrush"];

                // A 1px MDL2 stroke disappears against a gray button face at 16px.
                combined["CaptionGlyphWeight"] = flat ? FontWeights.Bold : FontWeights.Normal;

                // The About card only gets a caption band when the theme asks for one; 0 keeps
                // every other theme's card at the exact layout it already had.
                combined["AboutCaptionHeight"] = flat ? combined["DialogTitleBarHeight"] : 0.0;

                // The About card's close X, now the shared ChromeCloseButton style: on a flat
                // theme a small Win98 caption button sitting inside the 20px band (16x14 with a
                // 3px gap, next to the band's 2,2,2 inset), elsewhere the 28x26 corner slot the
                // bare glyph always occupied (matching KillerNotes' card).
                combined["AboutCloseWidth"]  = flat ? 16.0 : 28.0;
                combined["AboutCloseHeight"] = flat ? 14.0 : 26.0;
                combined["AboutCloseMargin"] = flat ? new Thickness(0, 5, 5, 0) : new Thickness(0, 6, 6, 0);

                // ...and the rest of the OverlayCloseButton split (Controls.xaml): the ordinary
                // themes keep the card X's ORIGINAL look - muted U+2715, red glyph on hover, no
                // fill - and 98SE gets the black bold E8BB on the gray caption-button face with
                // no hover change at all.
                combined["AboutCloseGlyph"] = flat ? "\uE8BB" : "\u2715";
                combined["AboutCloseFont"]  = flat ? new System.Windows.Media.FontFamily("Segoe MDL2 Assets")
                                                   : new System.Windows.Media.FontFamily("Segoe UI");
                combined["AboutCloseFg"] = flat ? combined["CaptionCloseBrush"] : combined["MutedTextBrush"];
                if (flat) combined["AboutCloseHoverFg"] = combined["CaptionCloseBrush"];
                else
                {
                    var closeRed = new SolidColorBrush(Color.FromRgb(0xE0, 0x44, 0x44)); closeRed.Freeze();
                    combined["AboutCloseHoverFg"] = closeRed;
                }

                // The About wordmark's hard 1px WHITE offset copy - the Win98 chiseled
                // letterpress, only on a flat theme whose blurred shadow copy is off.
                combined["AboutEmbossOpacity"] = flat ? 1.0 : 0.0;

                // The elevation halo is an overlay ring on every ordinary theme; a flat theme
                // hides it and marks an admin window by repainting the frame's outermost ring in
                // the accent instead (Elevation.cs).
                combined["ElevationHaloVisibility"] = flat ? Visibility.Collapsed : Visibility.Visible;

                // Menu and flyout shadows. Win98 context menus cast a HARD solid drop shadow -
                // the one shadow this otherwise-flat theme keeps - while
                // every ordinary theme keeps exactly the soft treatment it had: the ContextMenu
                // template's 12px blur at FlyoutShadowOpacity, and the flyout cards' shared
                // CardShadowEffect.
                combined["MenuShadowOpacity"] = flat ? 1.0 : combined["FlyoutShadowOpacity"];
                if (flat)
                {
                    // BlurRadius 5, not 0: fully hard-edged read as a black slab with too hard
                    // of an edge - this keeps the
                    // Win98 offset-shadow shape with just enough softening to sit right.
                    var hard = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 5, Direction = 315, Opacity = 0.35 };
                    hard.Freeze();
                    combined["MenuShadowEffect"] = hard;
                    combined["FlyoutCardEffect"] = hard;
                    combined["ComboPopupShadow"] = hard;
                }
                else
                {
                    // 22/4, the family flyout shadow, matching FlyoutCardEffect below. The menu
                    // template's halo is sized to exactly this; change one and the other follows.
                    var soft = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.5 };
                    soft.Freeze();
                    combined["MenuShadowEffect"] = soft;
                    // Built HERE, not read from CardShadowEffect - that key is only created
                    // further DOWN this method, so reading it stored null and every flyout card
                    // lost its shadow on the ordinary themes. Same recipe CardShadowEffect uses.
                    double cardFso = combined["FlyoutShadowOpacity"] is double cf ? cf : 1.0;
                    var cardShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.55 * cardFso };
                    cardShadow.Freeze();
                    combined["FlyoutCardEffect"] = cardShadow;
                    // The ComboBox dropdown's own shadow - the literal its template hardcoded.
                    var combo = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.55 };
                    combo.Freeze();
                    combined["ComboPopupShadow"] = combo;
                }

                // The elevated window's edge - see Elevation.cs ApplyElevationHalo: a 2px accent
                // band around the gray frame on a flat theme, the ordinary window edge otherwise.
                combined["ElevationEdgeBrush"] = flat ? combined["PrimaryBrush"] : combined["WindowEdgeBrush"];
                combined["ElevationEdgeThickness"] = flat ? new Thickness(2) : combined["WindowEdgeThickness"];

                // The Shortcuts card's content inset: the 24,20 it always had, plus top room for
                // the caption band on a flat theme.
                combined["ShortcutsContentMargin"] = flat ? new Thickness(24, 28, 24, 20)
                                                          : new Thickness(24, 20, 24, 20);

                // The details filename's family drop shadow - null on a flat theme, which casts
                // nothing (its 98SE depth comes from the hard white emboss copy instead).
                if (flat) combined["DetailsNameEffect"] = null;
                else
                {
                    var nameShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2, Direction = 270, Opacity = 0.6 };
                    nameShadow.Freeze();
                    combined["DetailsNameEffect"] = nameShadow;
                }

                // A ready-made pane shadow at this theme's opacity, or NULL on a flat theme.
                // Built per load and FROZEN: a DynamicResource inside a shared keyed Freezable's
                // Opacity does not reliably resolve, which is how a flat theme ended up casting a
                // full-strength shadow in KillerNotes.
                if (combined["PaneShadowOpacity"] is double pso && pso > 0)
                {
                    var paneShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 5, Direction = 270, Opacity = pso };
                    paneShadow.Freeze();
                    combined["PaneShadowEffect"] = paneShadow;
                }
                else combined["PaneShadowEffect"] = null;

                // The lighter BAR-tier shadow, for things that sit just proud of their surface -
                // the active tab, toolbars. Same rule: NULL on a flat theme, which is what makes
                // 98SE genuinely shadowless rather than shadowless-except-the-bits-I-missed.
                if (combined["BarShadowOpacity"] is double bso && bso > 0)
                {
                    var barShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 9, ShadowDepth = 0, Opacity = bso };
                    barShadow.Freeze();
                    combined["BarShadowEffect"] = barShadow;
                }
                else combined["BarShadowEffect"] = null;

                // The scrollbar thumb's HOVER treatment - the 2px shrink and the soft glow the
                // template hardcoded. On a flat theme the thumb is a raised beveled button that
                // fills its track, so shrinking it on hover pulled the fill out from under its
                // own bevel ring, and the glow put a shadow on a theme that has none anywhere
                // (there, the scrollbars should read raised, not flat). Defaults are
                // exactly the literals the template carried, so the other twelve do not move.
                if (!combined.Contains("ScrollThumbHoverMargin"))
                    combined["ScrollThumbHoverMargin"] = flat ? new Thickness(0) : new Thickness(2);
                if (flat) combined["ScrollThumbHoverEffect"] = null;
                else
                {
                    var thumbGlow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.3 };
                    thumbGlow.Freeze();
                    combined["ScrollThumbHoverEffect"] = thumbGlow;
                }

                // The heavy CARD shadow - dialogs, the About card, floating overlays. Scaled by
                // FlyoutShadowOpacity, so a flat theme gets NULL and 98SE's dialogs sit flat
                // against the window the way a Win98 dialog does.
                if (combined["FlyoutShadowOpacity"] is double fso && fso > 0)
                {
                    var cardShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.55 * fso };
                    cardShadow.Freeze();
                    combined["CardShadowEffect"] = cardShadow;
                }
                else combined["CardShadowEffect"] = null;

                // The ACTIVE tab's accent stripe. 3px across the top by default; a flat theme gets
                // none, because a Win98 tab is identified by its bevel and by being joined to the
                // pane, not by a colored bar. The padding compensates either way so the title
                // never shifts as a tab activates.
                if (!combined.Contains("TabStripeThickness"))
                    combined["TabStripeThickness"] = flat ? new Thickness(0) : new Thickness(0, 3, 0, 0);
                if (!combined.Contains("TabActivePadding"))
                    combined["TabActivePadding"] = flat ? new Thickness(12, 4, 5, 5)
                                                        : new Thickness(12, 1, 5, 5);

                // A TAB is the panel radius on its TOP corners only - the bottom two are where it
                // joins the pane and must stay square. Derived so it tracks PanelCornerRadius and
                // squares off automatically on a flat theme rather than needing its own token.
                if (!combined.Contains("TabCornerRadius"))
                {
                    double p = combined["PanelCornerRadius"] is CornerRadius pcr2 ? pcr2.TopLeft : 6.0;
                    combined["TabCornerRadius"] = new CornerRadius(p, p, 0, 0);
                }

                // The close button's hover block follows the CARD's radius on its own corner and
                // stays square on the three interior edges.
                if (!combined.Contains("CaptionCloseCornerRadius"))
                {
                    double tr = combined["WindowCornerRadius"] is CornerRadius wcr ? wcr.TopRight : 0.0;
                    combined["CaptionCloseCornerRadius"] = new CornerRadius(0, tr, 0, 0);
                }
            }

            // OutlineButton's hover fills with OutlineBtnBrush but used to put OnPrimaryBrush on
            // top of it. Those are two different tokens, and on any theme where the outline color
            // is a MID tone they disagree badly: white on Sepulchre's #4faaa8 measures 2.75:1,
            // Delirium 2.83, Black's #00ff66 just 1.36 (the Install button in
            // the portable badge was the one that showed it). Derived rather than hand-picked per
            // theme so it cannot drift, and computed HERE, after the accent overlay, because
            // picking an accent replaces OutlineBtnBrush.
            combined["OnOutlineBtnBrush"] = ReadableOn(combined["OutlineBtnBrush"] as SolidColorBrush,
                                                       combined["OnPrimaryBrush"] as SolidColorBrush);

            // The dual-pane focus ring's brush: the active tab's side/stripe borders, the band's
            // TabEdge verticals and the lit TabBarRing all draw with it. PrimaryBrush on every
            // ordinary theme, exactly as they always did - but a theme may state its own, and 98SE
            // states Transparent, because a Win98 tab is identified by its bevel and its join to
            // the page, never by an accent ring (dual pane). Computed HERE,
            // after the accent overlay, so it follows the picked accent like OnOutlineBtnBrush.
            if (!combined.Contains("TabActiveRingBrush"))
                combined["TabActiveRingBrush"] = combined["PrimaryBrush"];

            // Selected DataGrid CELL text - see DarkDataGridCell: the PrimaryBrush its selected
            // trigger always set, so nothing changes off 98SE, which states white. HERE, after
            // the accent overlay, for the same reason as TabActiveRingBrush above.
            if (!combined.Contains("GridSelectedTextBrush"))
                combined["GridSelectedTextBrush"] = combined["PrimaryBrush"];

            // Highlighted ComboBox item text - same shape: the PrimaryBrush the trigger always
            // set, white on 98SE where the highlight bar is the solid navy MenuHoverBrush.
            if (!combined.Contains("ComboHighlightTextBrush"))
                combined["ComboHighlightTextBrush"] = combined["PrimaryBrush"];

            // A dialog's caption band. TRANSPARENT by default, so the band shows the card's own
            // face and is invisible - the family look, where a dialog title blends into the
            // surface. A theme that genuinely wants a distinct caption declares UseDialogCaption
            // and gets TitleBarBrush instead. Computed HERE, after the accent overlay: reading it
            // earlier picks up the BASE TitleBarBrush, and the overlay then replaces that key
            // without touching this copy, so every dialog would keep the base theme's caption
            // color while the main window followed the accent.
            if (!combined.Contains("DialogTitleBarBrush"))
            {
                bool wantsCaption = combined.Contains("UseDialogCaption")
                                 && combined["UseDialogCaption"] is bool dc && dc;
                if (wantsCaption && combined.Contains("TitleBarBrush"))
                    combined["DialogTitleBarBrush"] = combined["TitleBarBrush"];
                else
                {
                    var clear = new SolidColorBrush(Colors.Transparent); clear.Freeze();
                    combined["DialogTitleBarBrush"] = clear;
                }
            }

            // The outline button's hover TEXT defaults to that AA-derived color. Set here rather
            // than in the contract block above, because OnOutlineBtnBrush does not exist until
            // this line - a theme that states its own OutlineHoverTextBrush (98SE wants plain
            // black) still wins, since this only fills a gap.
            if (!combined.Contains("OutlineHoverTextBrush"))
                combined["OutlineHoverTextBrush"] = combined["OnOutlineBtnBrush"];

            // Null-guarded (CS8602): Application.Current is null during design time and unit
            // hosting, and the nullable analysis flags the bare dereference. Nothing to merge
            // into without an application anyway.
            var app = Application.Current;
            if (app == null) return;
            var merged = app.Resources.MergedDictionaries;
            if (merged.Count > 0)
                merged[0] = combined;
            else
                merged.Add(combined);
        }
    }
}
