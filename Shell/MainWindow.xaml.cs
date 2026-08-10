using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;

namespace KillerShell.Shell
{
    // Search core: ctor/wiring, the scope picker, term/filter handlers, the search
    // loop, and engine callbacks. Everything else is partials:
    //   Tabs.cs      - tab lifecycle + strip drag physics
    //   Session.cs   - install flow, smart-Esc quit, tab persistence
    //   Results.cs   - result interactions, sorting, quick filter, pipe
    //   Export.cs    - CSV / HTML export
    //   Chrome.cs / ThemeFlyout.cs / About.cs / Language.cs - shell, theme, about, locale
    public partial class MainWindow : Window
    {
        // _tabs and _active used to live here. They are per-PANE state now (FilePane.Tabs /
        // FilePane.Active), reached through the same-named shims in Panes.cs so every call site
        // reads unchanged. Two panes each own their own strip and their own active search.

        public MainWindow()
        {
            InitializeComponent();

            // KillerUI / Grunge shell wiring.
            SourceInitialized += MainWindow_SourceInitialized;   // Chrome.cs
            ApplyGrainTexture();                                 // Chrome.cs
            Loaded += (_, _) => FadeInContent();                 // Chrome.cs
            UpdateThemeSwatchSelection();                        // ThemeFlyout.cs
            UpdateAccentSwatches();
            SyncTitleBarMetrics();                               // Chrome.cs
            // ApplyPaneMargins rides along: the pane's window-edge inset is PaneOuterMargin's RIGHT
            // now (8 by default, 0 on 98SE), so without re-running it a theme switch left the old
            // theme's gap down the right of the pane until something else forced a relayout.
            // RepaintIcons is DEFERRED to Background priority: it reloads a whole icon pack and
            // touches every visible row, and running it synchronously inside the theme swap was
            // the biggest slice of the freeze before the crossfade could
            // start. The crossfade ghost is opaque over the window, so the stale icons
            // repainting a beat later are never seen; without a ghost (startup, code-driven
            // switches) a one-pass delay on icon art is imperceptible anyway. The cheap,
            // layout-critical calls stay synchronous.
            //
            // The pack the icons currently come from, so RepaintIcons can tell a switch that
            // actually changed the art from the twelve-out-of-thirteen that did not. Seeded here
            // rather than at the field, because the field initializer runs before
            // InitializeComponent and this has to read the palette App already loaded
            // (App.xaml.cs calls ThemeManager.Initialize before the window is constructed).
            _iconPackFlat = FlatChrome;   // Chrome.cs
            Services.ThemeManager.ThemeChanged += () =>
            {
                UpdateThemeSwatchSelection(); UpdateAccentSwatches(); SyncTitleBarMetrics();
                ApplyPaneMargins(); LeftPane?.RefreshPaneClip(); RightPane?.RefreshPaneClip();
                // Timed so one theme click prints this next to CrossfadeSwap's heavy work and the
                // slow half of the pause before the fade can be identified instead of guessed at
                // (ThemeFlyout.cs TimedStep). DEBUG only; a release build prints nothing.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() => TimedStep("RepaintIcons", RepaintIcons)));
            };

            var ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            // Demo used to fake "v1.0.0" pre-release; versions are real now, so always show the truth.
            VersionLabel.Text = $"v{ver}";

            // Titlebar + About icons: ksh-icon.ico is multi-size, so pick the frame nearest
            // each display size (a raw Image Source=.ico can grab the 16px frame and
            // upscale it blurry - that was the mangled About icon).
            try
            {
                var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    new Uri("pack://application:,,,/Resources/ksh-icon.ico"),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                TitleIcon.Source = dec.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).First();
                AboutIcon.Source = dec.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 64)).First();
            }
            catch { /* icon missing - wordmark alone is fine */ }

            Pane.TabStrip.ItemsSource = _tabs;

            // What a shell is told about the window hosting it (Terminal/ShellEnv.cs). Before
            // any shell can be spawned, because a child inherits the environment as a COPY - a
            // variable published after the fact never reaches a terminal already open. This used
            // to run much later (still "before any shell," back when the earliest one could ever
            // start was an explicit F11/Ctrl+Shift+~ press well after the constructor finished),
            // but restore can now reopen a shell tab of its own (Session.cs TryRestoreTabs, via
            // TabHandoff.cs ApplyHandoff) - moved up here, ahead of THAT, rather than leave a
            // restored shell launching with no KS_STATE/KS_ACCENT and its prompt falling back to
            // hardcoded colors.
            InitShellEnv();

            // A window opened with Ctrl+N is a NEW window, not a resumed session. It runs as its
            // own process (NewWindow.cs), so without this it read the same saved tab list the
            // first window did and came up carrying every tab from last time.
            bool freshWindow = Array.Exists(Environment.GetCommandLineArgs(),
                a => string.Equals(a, "--new-window", StringComparison.OrdinalIgnoreCase));

            // An ELEVATED window does not restore the session either. It already refuses to SAVE
            // it (see Closing, below: both instances share one settings store, and an admin
            // window is a task you finish, not the one you live in) - not restoring is the mirror
            // of that and was simply missed. An elevated relaunch is started with --eventviewer /
            // --processes / --shell, never --new-window, so freshWindow was false and the admin
            // window came up carrying every tab from the ordinary one. Each restored TERMINAL tab
            // then asked for a non-elevated shell inside an elevated window, which OpenShell
            // answers by bouncing the request back out through OpenUnelevated - one explorer.exe
            // and one extra KillerShell PER RESTORED SHELL TAB. That is where the pile of Explorer
            // windows came from: seven tabs, seven windows; eight tabs, eight.
            if (DemoMode || freshWindow || IsElevated || !TryRestoreTabs())
                ActivateTab(CreateTab());   // Session.cs / Tabs.cs

            // Restore the saved app-wide accessibility size (AppScale.cs). After the tabs so
            // _active exists, though the restore path never writes a status line.
            InitAppScale();

            // Restore the saved results view mode and tile size (ResultsView.cs). Also after the
            // tabs, since applying the view reads _active to redraw the sort arrows.
            InitResultsView();

            // Search is an optional panel now, closed unless it was left open (SearchPanel.cs).
            InitSearchPanel();

            // Folder tree on the left, open unless it was closed (TreePanel.cs). Roots are the
            // ready drives; everything below loads on expand (FolderTree.cs).
            InitFolderTree();
            InitTreePanel();

            // ... and reopen the branches that were open last time (FolderTree.cs). Not awaited:
            // every level enumerates off the UI thread, and a window that waited on a slow
            // network branch coming back would be a window that does not open.
            _ = RestoreTreeExpansionAsync();

            // Saved locations, in the slide-up under the tree (Bookmarks.cs). After the tree so
            // its panel row exists, and after the tabs so the star can read the active folder.
            InitBookmarks();

            // Recently visited folders, behind the address bar's chevron (Recents.cs). After
            // bookmarks because it reuses their separator, and before any navigation runs.
            InitRecents();

            // Results density used to be restored here (Density.cs) - it is per-pane now, one
            // more field on the same ViewState as zoom and column widths, so InitResultsView
            // above restores it for both panes in the same pass.

            // Show-hidden and folders-on-top (ViewOptions.cs). Before nothing in particular -
            // they are read by the listing and the tree, both of which run later.
            InitViewOptions();

            // Details/preview strip, off by default (DetailsPane.cs). After the panes exist -
            // it reads pane.ResultsList for the current selection the first time it opens.
            InitDetailsPane();

            // Menubar hidden or showing (MenuBar.cs). After the panes exist and before the
            // first listing, so a hidden bar never flashes on screen at launch.
            InitMenuBar();

            // Chosen typefaces for the app and the terminal (Fonts.cs). The app slot overrides
            // the MonoFont resource, so this only has to beat the first render, not any
            // particular init - a DynamicResource repaints whenever the override lands.
            InitFonts();

            // Where new tabs open (AddressBar.cs).
            InitHomeFolder();

            Loaded += (_, _) =>
            {
                // Demo mode: no install badge (and fabricated tabs, DemoMode.cs).
                if (App.IsPortable() && !DemoMode) PortableBadge.Visibility = Visibility.Visible;
                if (DemoMode) GenerateDemoData();

                // A first-run tab starts at Home rather than as an empty search form. Deferred
                // to Loaded rather than done in the ctor because navigating reveals the folder
                // in the tree, and the tree's roots are not built until InitFolderTree above has
                // run. Restored tabs and piped tabs are left exactly as they were.
                if (!DemoMode && _active.PipeFiles == null && StartupPaths.Count == 0)
                {
                    // A restored folder tab carries the folder it was closed in but not its
                    // listing (Session.cs), so it is re-listed here. Without this it came back as
                    // an empty search form with a path sitting in the location box, because
                    // CaptureTab also stores a browsed folder as the search root and a non-empty
                    // RootPath used to be enough to skip this block entirely.
                    string back = _active.CurrentFolder;
                    bool resume = _active.IsBrowsing && back.Length > 0
                                  && (IsThisPc(back) || Directory.Exists(back));

                    // A session written before the browse fields existed has no CurrentFolder at
                    // all, only the RootPath that CaptureTab left behind - so the test for "is
                    // there anything to show here" is whether the tab has a pattern to run, not
                    // whether it has a root. A tab with nothing to search must never be left
                    // sitting on an empty pane: browse its scope if that is a real folder, and
                    // fall back to Home if it is not.
                    bool hasSearch = _active.Groups.Any(
                        g => g.Terms.Any(t => !string.IsNullOrWhiteSpace(t.Pattern)));

                    if (resume) _ = NavigateTo(back, record: false);                   // Browse.cs
                    else if (!hasSearch)
                        _ = NavigateTo(Directory.Exists(_active.RootPath)              // Browse.cs
                                       ? _active.RootPath : HomeFolder);
                }

                // A path from Explorer wins over Home: it is the thing the user actually
                // asked for, and it reuses that first tab rather than opening beside it.
                if (!DemoMode) OpenStartupPaths();   // StartupPaths.cs

                ApplyElevationHalo();   // Elevation.cs - mark an admin window before it shows
                ApplyStartupShell();    // and open the shell an elevated relaunch asked for
                ApplyStartupTearOut();  // TabTearOut.cs - or the Processes/document tab a tear-out asked for
            };
            Closing += (_, _) =>
            {
                // No fade here: Session.cs's OnClosing override already does it (stage 2), and
                // it runs BEFORE this handler because raising Closing is the last thing that
                // override does. A second fade started from here re-cancelled a close the
                // override had already taken charge of.
                StopWatching();                            // BrowseWatcher.cs
                DisposeShellEnv();                         // Terminal/ShellEnv.cs

                // An elevated window does NOT write the session back. Both instances share one
                // settings store, so whichever quit last would clobber the other's remembered
                // tabs - and the admin window is a task you finish, not the one you live in.
                if (!DemoMode && !IsElevated) SaveTabsOnExit();   // Session.cs
            };

            // The two thumb buttons on a mouse, at the WINDOW level so they work over a folder
            // listing, the tree and a terminal alike - a shell has no use for them and Windows
            // has meant Back and Forward by them for twenty years. Preview, so the terminal's
            // own mouse handling never sees them first.
            PreviewMouseDown += (_, e) =>
            {
                if (e.ChangedButton == System.Windows.Input.MouseButton.XButton1)
                {
                    NavBack_Click(this, new RoutedEventArgs());      // Browse.cs
                    e.Handled = true;
                }
                else if (e.ChangedButton == System.Windows.Input.MouseButton.XButton2)
                {
                    NavForward_Click(this, new RoutedEventArgs());   // Browse.cs
                    e.Handled = true;
                }
            };

            // ThemeFlyout is a Button.ContextMenu now (matching LangMenu, KillerPDF's pattern),
            // and a ContextMenu's own popup already closes itself on an outside click or when
            // the window is deactivated/moved - the hand-rolled close-tracking a raw Popup
            // needed is gone along with the Popup.
        }

        /// <summary>Whether the icon art on screen came from the flat theme's pack. Seeded in the
        /// constructor and only ever written by RepaintIcons below.</summary>
        private bool _iconPackFlat;

        /// <summary>
        /// Redraw every surface that gets its art from IconCache, after a theme change has moved
        /// the app between the brand pack and the period 98 pack (IconCache.Pack).
        ///
        /// Needed because IconCache is asked through value converters and one-way bindings on the
        /// row objects. Nothing about a row CHANGES when the theme does - the same folder is still
        /// the same folder - so no binding re-evaluates on its own and every icon in the app keeps
        /// the outgoing theme's art until the list is rebuilt for some unrelated reason. Refresh is
        /// the cheap forced re-run: the bitmaps themselves are already decoded and cached per pack,
        /// so this is a repaint and not a reload.
        ///
        /// A NO-OP unless the pack really moved. IconCache has exactly one theme input -
        /// IconCache.Pack, which is "98/" on a flat theme and "" on every other, read straight off
        /// MainWindow.FlatChrome - so only a switch INTO or OUT OF 98SE can change a single pixel
        /// of this art. Every accent dot and eleven of the twelve remaining themes leave the pack
        /// identical, and this used to run for all of them: four Items.Refresh calls that tore
        /// down and regenerated every realized container in the file listing, both tab strips, the
        /// folder tree and the saved locations, forcing a SECOND full measure/arrange/render pass
        /// over them, in order to end up drawing byte-identical bitmaps. That whole pass ran
        /// inside the gap between the theme swap and the crossfade, which is the gap the user sees
        /// as a stall.
        ///
        /// Nothing else in the row templates needs the Refresh. The two converters the strip uses
        /// are TabFolderIconConverter, which asks IconCache and nothing else, and
        /// TabChamferConverter, which takes the TabChamfer theme scalar as a BOUND value and so
        /// re-evaluates itself when the resource changes. Every other theme-reactive part of a row
        /// is a DynamicResource brush, which repaints on its own.
        /// </summary>
        private void RepaintIcons()
        {
            bool flat = FlatChrome;              // Chrome.cs - the only thing IconCache.Pack reads
            if (flat == _iconPackFlat) return;   // same art either side of the switch
            _iconPackFlat = flat;

            // Per pane: the listing and the tab strip both live on FilePane, and dual pane means
            // both of them exist twice. RightPane is declared in the XAML so it is never null, only
            // collapsed while the split is closed - refreshing it costs nothing and means the icons
            // are already right the first time it is shown.
            foreach (var pane in new[] { LeftPane, RightPane })
            {
                if (pane == null) continue;
                try
                {
                    pane.ResultsList?.Items.Refresh();
                    pane.TabStrip?.Items.Refresh();
                }
                catch { /* a list mid-rebuild refuses Refresh; the rebuild draws the new art anyway */ }
            }

            // Window-level: the tree and the saved locations are shared by both panes.
            try { FolderTree?.Items.Refresh();    } catch { }
            try { BookmarksList?.Items.Refresh(); } catch { }
        }

        private void VersionLabel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowAboutOverlay();  // About.cs

        private void Wordmark_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://killershell.net") { UseShellExecute = true });
            e.Handled = true;
        }

        // ═══════════════════════════════════════════════════════════
        //  SCOPE - folder picker
        // ═══════════════════════════════════════════════════════════
        // Clicking the location row now starts an address edit instead of opening the picker;
        // that handler lives in AddressBar.cs. The picker is still reachable from Ctrl+O and
        // from the search panel's own browse button below.
        private void BrowseRoot_Click(object sender, RoutedEventArgs e)
            => OpenFolderPicker();

        private void OpenFolderPicker()
        {
            var dlg = new FolderPickerDialog(Pane.RootPathBox.Text) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            string? picked = dlg.SelectedPath;
            if (picked is null || picked.Length == 0) return;

            Pane.RootPathBox.Text    = picked;
            Pane.ScopePathLabel.Text = picked;
            _active.RootPath    = picked;
            _active.Title       = ToTabTitle(picked);
            // Picking a folder is the escape hatch from a piped scope.
            _active.PipeFiles   = null;
            _active.PipeLabel   = string.Empty;

            // Picking a folder now GOES there as well as scoping the search to it. That is the
            // whole shift: the folder you are looking at and the folder a search would cover are
            // the same folder, so there is nothing to keep in sync.
            _ = NavigateTo(picked);   // Browse.cs
        }

        // Tab title = the search location, home-relative: C:\Users\Demo\code -> ~\code.
        // Distinct folders under home stay distinct instead of all collapsing to a leaf name.
        private static string ToTabTitle(string path)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[home.Length..].TrimStart('\\');
                return rest.Length == 0 ? "~" : "~\\" + rest;
            }
            return path;
        }

        // ═══════════════════════════════════════════════════════════
        //  SEARCH TERMS
        // ═══════════════════════════════════════════════════════════
        private void AddTerm_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TermGroup g)
                g.Terms.Add(new SearchTerm());
        }

        private void RemoveTerm_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SearchTerm term)
            {
                var groups = _active.Groups;
                var g = groups.FirstOrDefault(gr => gr.Terms.Contains(term));
                if (g == null) return;
                if (groups.Sum(gr => gr.Terms.Count) > 1) g.Terms.Remove(term);
                if (g.Terms.Count == 0 && groups.Count > 1) groups.Remove(g);
            }
        }

        private void AddGroup_Click(object sender, RoutedEventArgs e)
        {
            var g = new TermGroup();
            g.Terms.Add(new SearchTerm());
            _active.Groups.Add(g);
        }

        private void RemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TermGroup g && _active.Groups.Count > 1)
                _active.Groups.Remove(g);
        }

        private void ToggleGroupMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TermGroup g)
                g.Mode = g.Mode == TermGroup.GroupMode.Or ? TermGroup.GroupMode.And : TermGroup.GroupMode.Or;
        }

        private void ToggleMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SearchTerm term)
                term.Mode = term.Mode == SearchTerm.SearchMode.FileName
                    ? SearchTerm.SearchMode.Content
                    : SearchTerm.SearchMode.FileName;
        }

        // ═══════════════════════════════════════════════════════════
        //  FILTERS
        // ═══════════════════════════════════════════════════════════
        private void AddFilter_Click(object sender, RoutedEventArgs e)
            => _active.Filters.Add(new SearchFilter());

        private void RemoveFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SearchFilter f)
                _active.Filters.Remove(f);
        }

        private void ToggleFilterCondition_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SearchFilter f)
                f.ConditionIndex = f.ConditionIndex == 0 ? 1 : 0;
        }

        // "advanced" accordion (include/exclude/case): slides open/closed by animating
        // MaxHeight (150ms, eased) with an opacity fade riding along.
        private void AdvancedToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            bool show = AdvancedPanel.Visibility != Visibility.Visible;
            // MDL2 chevron down / right, from codepoints so the source stays ASCII.
            AdvancedChevron.Text = ((char)(show ? 0xE70D : 0xE76C)).ToString();

            var ease = new System.Windows.Media.Animation.QuadraticEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            if (show)
            {
                AdvancedPanel.Visibility = Visibility.Visible;
                AdvancedPanel.Measure(new Size(AdvancedPanel.ActualWidth > 0 ? AdvancedPanel.ActualWidth : 300,
                                               double.PositiveInfinity));
                double target = AdvancedPanel.DesiredSize.Height;
                var grow = new System.Windows.Media.Animation.DoubleAnimation(0, target,
                    TimeSpan.FromMilliseconds(Anim.FadeMs)) { EasingFunction = ease };
                grow.Completed += (_, _) =>
                {
                    AdvancedPanel.BeginAnimation(MaxHeightProperty, null);
                    AdvancedPanel.MaxHeight = double.PositiveInfinity;
                };
                AdvancedPanel.BeginAnimation(MaxHeightProperty, grow);
                AdvancedPanel.BeginAnimation(OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Anim.FadeMs)));
            }
            else
            {
                var shrink = new System.Windows.Media.Animation.DoubleAnimation(AdvancedPanel.ActualHeight, 0,
                    TimeSpan.FromMilliseconds(Anim.FadeMs)) { EasingFunction = ease };
                shrink.Completed += (_, _) =>
                {
                    AdvancedPanel.Visibility = Visibility.Collapsed;
                    AdvancedPanel.BeginAnimation(MaxHeightProperty, null);
                    AdvancedPanel.MaxHeight = double.PositiveInfinity;
                };
                AdvancedPanel.BeginAnimation(MaxHeightProperty, shrink);
                AdvancedPanel.BeginAnimation(OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Anim.FadeMs)));
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  PATTERN HELP CARD
        // ═══════════════════════════════════════════════════════════
        private void PatternHelp_Click(object sender, RoutedEventArgs e)
        {
            PatternHelpOverlay.Visibility = Visibility.Visible;
            Anim.FadeIn(PatternHelpOverlay);   // the standard 150ms fade, like About
        }

        private void PatternHelpClose_Click(object sender, RoutedEventArgs e)
            => FadeOverlayOut(PatternHelpOverlay);   // About.cs helper

        private void PatternHelpOverlay_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => FadeOverlayOut(PatternHelpOverlay);

        private void PatternHelpCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => e.Handled = true;   // clicks on the card don't dismiss it

        // ═══════════════════════════════════════════════════════════
        //  SHORTCUTS CARD (F1) - family standard, same shape as above
        // ═══════════════════════════════════════════════════════════
        private void Shortcuts_Click(object sender, RoutedEventArgs e)
        {
            // Restores whichever view you last had open, and builds it on first use
            // (KeyboardMapOverlay.cs / ShortcutsOverlay.cs).
            ApplyPersistedShortcutView();

            ShortcutsOverlay.Visibility = Visibility.Visible;
            Anim.FadeIn(ShortcutsOverlay);
        }

        private void ShortcutsClose_Click(object sender, RoutedEventArgs e)
            => FadeOverlayOut(ShortcutsOverlay);   // About.cs helper

        private void ShortcutsOverlay_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => FadeOverlayOut(ShortcutsOverlay);

        private void ShortcutsCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => e.Handled = true;   // clicks on the card don't dismiss it

        // ═══════════════════════════════════════════════════════════
        //  SEARCH / STOP  (per tab - background tabs keep searching)
        // ═══════════════════════════════════════════════════════════
        private async void Search_Click(object sender, RoutedEventArgs e)
        {
            var tab = _active;

            if (tab.IsSearching)
            {
                tab.Cts?.Cancel();
                return;
            }

            CaptureTab(tab);

            string root = tab.RootPath.Trim();
            if (tab.PipeFiles == null && (string.IsNullOrEmpty(root) || !Directory.Exists(root)))
            {
                // No folder picked yet? Don't scold - open the picker and carry on.
                OpenFolderPicker();
                root = tab.RootPath.Trim();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            }

            var activeGroups = tab.Groups
                .Where(g => g.Terms.Any(t => !string.IsNullOrWhiteSpace(t.Pattern)))
                .ToList();
            var activeFilters = tab.Filters.Where(f => f.IsActive).ToList();
            if (activeGroups.Count == 0 && activeFilters.Count == 0)
            {
                SetTabStatusKey(tab, "Str_Status_NoTerms");
                return;
            }

            tab.Results.Clear();
            foreach (var t in tab.Groups.SelectMany(g => g.Terms)) t.ResetCount();
            tab.ScannedLabel = string.Empty;
            tab.StatsLabel   = string.Empty;
            tab.QueryLabel   = BuildQueryLabel(activeGroups, activeFilters);
            tab.IsSearching  = true;
            ApplySort(tab);   // strips the view sort for the run - see ApplySort
            if (tab == _active)
            {
                Pane.ScannedText.Text       = string.Empty;
                Pane.ScannedText.Visibility = Visibility.Visible;
                Pane.StatsText.Text         = string.Empty;
                Pane.QueryText.Text         = tab.QueryLabel;
                Pane.ResultsHeader.Text     = Loc("Str_Lbl_Results");
                ApplyStatusTone(tab.StatusKey);   // amber for the duration of the run
                UpdatePaneStatusBar();
                SearchButton.Content   = Loc("Str_Btn_Stop");
                SetExpandAllLabel(false);
            }

            tab.Cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            try
            {
                await tab.Engine.SearchAsync(
                    root, activeGroups, activeFilters, tab.IncludePatterns, tab.ExcludePatterns,
                    tab.CaseSensitive, tab.Cts.Token, tab.PipeFiles);

                // The engine's final batch is still queued on the dispatcher at this point -
                // let it land BEFORE reading Results.Count, or "Done - 0 file(s) matched"
                // shows next to a full list.
                await Dispatcher.InvokeAsync(() => { },
                    System.Windows.Threading.DispatcherPriority.Background);

                sw.Stop();
                if (tab.Cts.IsCancellationRequested)
                    SetTabStatusKey(tab, "Str_Status_Stopped");
                else
                    SetTabStatusKey(tab, "Str_Status_Done",
                        sw.Elapsed.TotalSeconds.ToString("0.00"), tab.Results.Count);
            }
            catch (OperationCanceledException)
            {
                SetTabStatusKey(tab, "Str_Status_Stopped");
            }
            finally
            {
                tab.IsSearching = false;
                ApplySort(tab);   // the run's deferred sort lands here, in one pass
                if (tab == _active)
                {
                    SearchButton.Content        = Loc("Str_Btn_Search");
                    Pane.ScannedText.Visibility = Visibility.Collapsed;
                    Pane.ResultsHeader.Text     = string.Format(Loc("Str_Lbl_ResultsCount"), tab.Results.Count);
                    // Done/Stopped were written while IsSearching was still true (they are set
                    // in the try, this runs in the finally), so the light is still amber from
                    // the run. Re-apply the tone now that the flag is down or it never goes
                    // back to green.
                    ApplyStatusTone(tab.StatusKey);
                    UpdatePaneStatusBar();
                }
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            var tab = _active;
            tab.Results.Clear();
            foreach (var t in tab.Groups.SelectMany(g => g.Terms)) t.ResetCount();
            tab.ScannedLabel = string.Empty;
            tab.StatsLabel   = string.Empty;
            tab.QueryLabel   = string.Empty;
            Pane.ScannedText.Visibility = Visibility.Collapsed;
            Pane.StatsText.Text         = string.Empty;
            Pane.QueryText.Text         = string.Empty;
            Pane.ResultsHeader.Text     = Loc("Str_Lbl_Results");
            UpdatePaneStatusBar();
            SetTabStatusKey(tab, "Str_Status_Cleared");
        }

        // "name: invoice  OR  content: foo  |  extension is pdf, over 100 MB" - built from
        // the same localized words the dropdowns show.
        private string BuildQueryLabel(List<TermGroup> groups, List<SearchFilter> filters)
        {
            var parts = new List<string>();
            foreach (var g in groups)
            {
                var terms = g.Terms.Where(t => !string.IsNullOrWhiteSpace(t.Pattern))
                    .Select(t => $"{t.ModeName}: {t.Pattern.Trim()}");
                string joiner = g.Mode == TermGroup.GroupMode.And
                    ? $"  {Loc("Str_Join_And")}  " : $"  {Loc("Str_Join_Or")}  ";
                parts.Add(string.Join(joiner, terms));
            }
            string q = string.Join("  +  ", parts.Where(p => p.Length > 0));

            var fparts = filters.Select(DescribeFilter).Where(s => s.Length > 0).ToList();
            if (fparts.Count > 0)
                q = q.Length > 0 ? $"{q}  |  {string.Join(", ", fparts)}" : string.Join(", ", fparts);
            return q;
        }

        private string DescribeFilter(SearchFilter f) => f.FieldIndex switch
        {
            SearchFilter.FieldExt =>
                $"{Loc("Str_Filter_Ext")} {Loc(f.ConditionIndex == 0 ? "Str_Cond_Is" : "Str_Cond_IsNot")} {f.Text.Trim()}",
            SearchFilter.FieldDate => f.Date.HasValue
                ? $"{Loc(f.ConditionIndex == 0 ? "Str_Cond_Before" : "Str_Cond_After")} {f.Date.Value:yyyy-MM-dd}"
                : string.Empty,
            _ =>
                $"{Loc(f.ConditionIndex == 0 ? "Str_Cond_Larger" : "Str_Cond_Smaller")} {f.SizeText.Trim()} {(f.UnitIndex == SearchFilter.UnitMb ? "MB" : "KB")}",
        };

        // Releasing a modifier drops the keyboard preview back a layer. Nothing else listens for
        // key-up; this exists purely so the board follows the hand (KeyboardMapOverlay.cs).
        private void Window_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            KbSyncLayerFromModifiers();

            // PrintScreen in an ELEVATED window (KeyUp, not KeyDown - WPF only ever raises the
            // up event for PrtScn): the snip overlay Windows binds to the key runs unelevated
            // and cannot come up over an elevated foreground window, so the key silently did
            // nothing here. Hand the request to the default snipping tool
            // through explorer.exe - the same de-elevation hand-off the shell tabs already use -
            // so it launches at normal integrity and can take the shot.
            if (e.Key == System.Windows.Input.Key.Snapshot && IsElevated)
            {
                try
                {
                    // Synthesize Win+Shift+S rather than launching anything: that chord is a
                    // REGISTERED hotkey (the snip overlay's own), and registered hotkeys fire
                    // regardless of the foreground window's integrity level - unlike the
                    // explorer.exe ms-screenclip: hand-off tried first, which did nothing here
                    // (PrtScn still appeared dead). This routes the key press to
                    // exactly whatever the user's default snipping tool is.
                    const byte VK_LWIN = 0x5B, VK_SHIFT = 0x10, VK_S = 0x53;
                    const uint KEYEVENTF_KEYUP = 0x0002;
                    keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_S, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_S, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                catch
                {
                    // The built-in PrtScn full-screen clipboard copy still happened; nothing
                    // useful to report if the synthetic chord could not be sent.
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        /// <summary>
        /// True when the chord being handled belongs to whichever applet currently owns the
        /// keyboard rather than to the window, so the window's own branch must stand down.
        /// </summary>
        /// <remarks>
        /// This is a PREVIEW handler on the window: it tunnels root-to-leaf and therefore sees
        /// every key BEFORE the focused tab's own grid, editor or terminal does. A chord bound
        /// in both places is decided here by default, and the applet's meaning can never fire.
        /// Two chords are in that position:
        ///
        ///   Ctrl+Shift+A - add a search term here; Run as administrator on a Processes row
        ///                  (ProcessListControl.cs Grid_PreviewKeyDown); Select all in a shell
        ///                  (Terminal/TerminalControl.cs HandleTerminalChord).
        ///   Ctrl+Shift+C - Copy as path here; Copy in a shell (same handler).
        ///
        /// The blanket handover guards at the top of Window_PreviewKeyDown do already bow out
        /// for a focused shell or Processes grid, because neither letter is in IsWindowChord
        /// (TerminalTabs.cs) - so in practice the applet does receive both today. That is a
        /// load-bearing side effect of a list written to answer a different question: adding
        /// either letter to IsWindowChord for some unrelated reason would silently take
        /// "Run as administrator" and the terminal's Copy away again, from a file two folders
        /// over. These gates state the rule at the chord itself so it cannot be broken at a
        /// distance.
        ///
        /// Nothing is renamed to resolve the overlap: the meanings live in different SCOPES now
        /// (KsScope / KsAll, ShortcutsOverlay.cs), which is also how the shortcuts card and the
        /// keyboard map are able to show both.
        /// </remarks>
        private bool ChordOwnedByFocus(params KsScope[] owners)
        {
            var scope = KsFocusScope;   // ShortcutsOverlay.cs
            foreach (var s in owners)
                if (s == scope) return true;
            return false;
        }

        // Global keys: Enter runs the search, Esc closes the filter bar or stops a
        // running search, Ctrl+F opens the results quick-filter.
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var mods  = System.Windows.Input.Keyboard.Modifiers;
            bool ctrl  = (mods & System.Windows.Input.ModifierKeys.Control) != 0;
            bool shift = (mods & System.Windows.Input.ModifierKeys.Shift)   != 0;
            bool alt   = (mods & System.Windows.Input.ModifierKeys.Alt)     != 0;

            // Holding a modifier previews that layer on the visual keyboard, so a chord can be
            // found by pressing Ctrl rather than by reading. No-op unless the board is showing
            // (KeyboardMapOverlay.cs), and deliberately BEFORE the handling below so it still
            // runs for chords that go on to be swallowed.
            KbSyncLayerFromModifiers();

            // A FOCUSED SHELL OWNS THE KEYBOARD.
            //
            // This is a PREVIEW handler on the window, so it tunnels from the root DOWN and
            // runs before the terminal ever sees the key. Without this guard the app's own
            // bindings win every time: Enter ran a search instead of submitting the command
            // line, and Backspace navigated instead of deleting a character. Anything a shell
            // could plausibly want - which is nearly everything, including bare letters, Enter,
            // Backspace, Tab, Esc, arrows and Ctrl+C - has to pass straight through.
            //
            // The exceptions are the chords that manage the WINDOW rather than the shell, and
            // they are listed rather than inferred: a shell has no opinion about which tab is
            // showing, so those stay with the app.
            if (TerminalHasFocus && !IsWindowChord(e, ctrl, shift, alt)) return;

            // A FOCUSED DOCUMENT OWNS THE KEYBOARD too, for exactly the same reasons and with
            // exactly the same list - plus Ctrl+S, which the shell cannot be given because over
            // a pty it is XOFF (EditorTabs.cs IsEditorChord).
            if (EditorHasFocus && !IsEditorChord(e, ctrl, shift, alt)) return;

            // A FOCUSED TASK MANAGER OWNS THE KEYBOARD too, for the same reason - its filter box
            // is a genuine text-editing surface, and a bare letter typed into it has to reach the
            // box rather than being read as a shortcut. It reuses the SHELL's list as-is rather
            // than growing its own or the editor's: there is no pty to protect from Ctrl+S/XOFF
            // and no document to protect from Ctrl+G, so IsWindowChord's base list is already the
            // whole answer (ProcessTabs.cs).
            if (ProcessListHasFocus && !IsWindowChord(e, ctrl, shift, alt)) return;

            // A FOCUSED EVENT VIEWER OWNS THE KEYBOARD too, for the same reason as the Task
            // Manager just above - its log/level pickers and filter box are genuine input
            // surfaces, and a bare letter typed into the filter has to reach it rather than being
            // read as a shortcut (EventViewerTabs.cs).
            if (EventViewerHasFocus && !IsWindowChord(e, ctrl, shift, alt)) return;

            // A FOCUSED REGISTRY EDITOR OWNS THE KEYBOARD too, for the same reason as Task
            // Manager/Event Viewer just above - its address bar and find box are genuine input
            // surfaces, and a bare letter typed into either has to reach them rather than being
            // read as a shortcut (RegistryEditorTabs.cs).
            if (RegistryEditorHasFocus && !IsWindowChord(e, ctrl, shift, alt)) return;

            // A FOCUSED STORAGE ANALYZER OWNS THE KEYBOARD too: its target box is a real text
            // surface, and it carries its own single-key map over the treemap - D depth, M min
            // size, C color mode, Backspace/Home/Enter to zoom, Delete to recycle
            // (StorageAnalyzerControl.OnPreviewKeyDown). Same handover the three tool tabs
            // above already use (StorageTabs.cs).
            if (StorageAnalyzerHasFocus && !IsWindowChord(e, ctrl, shift, alt)) return;

            // Alt+1-0 jumps to a saved location. Alt chords arrive as Key.System with the real
            // key parked in SystemKey, so they have to be unwrapped before anything can match -
            // and they are checked first, ahead of every e.Key test below, which would all see
            // Key.System and never fire. NumPad is deliberately excluded: Alt+numpad digits are
            // Windows' own character-entry sequence.
            if (alt && !ctrl && !shift)
            {
                var real = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                if (real >= System.Windows.Input.Key.D0 && real <= System.Windows.Input.Key.D9)
                {
                    // 0 is the tenth slot, the way it sits last on the number row.
                    int slot = real == System.Windows.Input.Key.D0 ? 10 : real - System.Windows.Input.Key.D0;
                    JumpToBookmark(slot);   // Bookmarks.cs
                    e.Handled = true;
                    return;
                }

                // Alt+D is Explorer's address-bar chord and costs nothing to honor alongside
                // Ctrl+L, so muscle memory from either lineage works.
                if (real == System.Windows.Input.Key.D)
                {
                    BeginEditAddress();   // AddressBar.cs
                    e.Handled = true;
                    return;
                }

                // Alt+Left / Right / Up: Explorer's navigation chords. These had no binding at
                // all - Back, Forward and Up were reachable only by clicking the toolbar, which
                // is the first thing a hand trained on Explorer reaches for and misses.
                if (real == System.Windows.Input.Key.Left)
                {
                    NavBack_Click(this, new RoutedEventArgs());      // Browse.cs
                    e.Handled = true;
                    return;
                }
                if (real == System.Windows.Input.Key.Right)
                {
                    NavForward_Click(this, new RoutedEventArgs());   // Browse.cs
                    e.Handled = true;
                    return;
                }
                if (real == System.Windows.Input.Key.Up)
                {
                    NavUp_Click(this, new RoutedEventArgs());        // Browse.cs
                    e.Handled = true;
                    return;
                }

                // Alt+P: Explorer's own preview-pane toggle, and free in this app - reused here
                // so it already means roughly the same thing to anyone coming from Explorer
                // (DetailsPane.cs). Acts on the focused pane's own strip.
                if (real == System.Windows.Input.Key.P)
                {
                    DetailsPaneToggle_Click(Pane);   // DetailsPane.cs
                    e.Handled = true;
                    return;
                }
            }

            // Backspace goes Back, the way it always has in Explorer. Guarded on text input, or
            // it would eat the character you were deleting in the address bar or a term box.
            if (!ctrl && !alt && e.Key == System.Windows.Input.Key.Back
                && e.OriginalSource is not TextBox && e.OriginalSource is not ComboBox)
            {
                NavBack_Click(this, new RoutedEventArgs());   // Browse.cs
                e.Handled = true;
                return;
            }

            if (ctrl && !shift && e.Key == System.Windows.Input.Key.B)
            {
                BookmarksBtn_Click(this, new RoutedEventArgs());   // Bookmarks.cs
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.H)
            {
                // Show hidden and system items. This had no chord at all - only the toolbar
                // toggle, which is also the first thing shed into the overflow chevron on a
                // narrow pane, so on a split window it could be two clicks deep. Ctrl+H is what
                // other file managers use for it and it was free (Ctrl+Shift+H is the hash).
                ShowHidden_Click(this, new RoutedEventArgs());     // ViewOptions.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F && ctrl && !shift)
            {
                ShowResultFilterBar();   // Results.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F && ctrl && shift)
            {
                PipeButton_Click(this, new RoutedEventArgs());   // Results.cs
                e.Handled = true;
            }
            else if (ctrl && !shift && !alt && e.Key == System.Windows.Input.Key.OemComma)
            {
                // Ctrl+comma: edit the preferred PowerShell host's $PROFILE (ProfileMenu.cs).
                // A chord rather than a bare key even though single keys are the house style -
                // F1 through F12 are all spoken for, and Ctrl+comma is what most things that
                // have a settings key use.
                EditPreferredProfile();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.G && ctrl && !shift && _active.Editor != null)
            {
                // Go to line, the chord every editor uses. Guarded on there being a document for
                // the same reason Ctrl+S is (EditorBar.cs).
                EditorGoto_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.S && ctrl && !shift && _active.Editor != null)
            {
                // Ctrl+S saves the document. Guarded on there BEING one rather than handled
                // unconditionally, so the chord stays free on every other kind of tab instead of
                // being quietly swallowed by a no-op (EditorTabs.cs).
                SaveActiveEditor();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.S && ctrl && shift)
            {
                // Search is an optional panel now, so it needs a way in from the keyboard
                // (SearchPanel.cs). The chevron's tooltip names this chord.
                ToggleSearchPanel();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F1)
            {
                // F1: the shortcuts card, same as every other app in the family.
                Shortcuts_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F5)
            {
                // Explorer's Refresh, and it resolves itself: F5 refreshes whatever the tab is
                // showing. A browsed folder re-lists off disk, a search tab re-runs its search -
                // which is what F5 already did, so nothing was taken away. Enter still runs a
                // search from the panel.
                if (_active != null && _active.IsBrowsing && !string.IsNullOrEmpty(_active.CurrentFolder))
                    _ = NavigateTo(_active.CurrentFolder!, record: false);   // Browse.cs
                else
                    Search_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F4 && !ctrl && !shift && !alt)
            {
                // Plain F4: the Storage Analyzer tab, singleton same as the rail icon
                // (OpenStorageAnalyzer, StorageTabs.cs). F4 took over from address-bar edit,
                // exactly the handover BACKLOG.md reserved: that action keeps its two working
                // aliases, Ctrl+L and Alt+D, so nothing was lost.
                OpenStorageAnalyzer();   // StorageTabs.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F4 && ctrl && !shift && !alt)
            {
                // Ctrl+F4: the same Storage Analyzer, elevated - an elevated scan sees the
                // folders an ordinary token gets Access Denied on. Same relaunch shape as
                // Ctrl+F9 for Processes ("--storage" flag, runas verb, reuse of an existing
                // elevated window - Elevation.cs RelaunchElevatedStorage).
                RelaunchElevatedStorage();   // Elevation.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F9 && !ctrl && !shift && !alt)
            {
                // Plain F9: the Processes tab, singleton same as the rail icon
                // (OpenTaskManager, ProcessTabs.cs). F9 took over from F11 -
                // export moved off F9 onto Ctrl+Alt+E below to make room for this. F11 itself went
                // to the Performance tab below rather than staying unbound.
                OpenTaskManager();   // ProcessTabs.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F9 && ctrl && !shift && !alt)
            {
                // Ctrl+F9: the same Processes tab, elevated - relaunches KillerShell through UAC
                // and lands directly on it, the same "--processes" flag an unelevated tear-out
                // already uses to reopen the tab in a fresh window (TabTearOut.cs
                // ApplyStartupTearOut), just started with the runas verb instead of plainly
                // (Elevation.cs RelaunchElevatedProcesses). Mirrors F8 / Ctrl+F8 for the shell.
                RelaunchElevatedProcesses();   // Elevation.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F11 && !ctrl && !shift && !alt)
            {
                // Plain F11: the Performance Monitor tab, singleton same as the rail icon
                // (OpenPerformanceMonitor, PerformanceTabs.cs). No Ctrl+F11 - unlike Processes and
                // Event Viewer, Performance needs no elevated variant: every counter it reads
                // (CPU/RAM/network/disk via PerformanceCounter, the one-time hardware inventory
                // via WMI) is available to an ordinary user account, so there is nothing an
                // elevated relaunch would unlock. BACKLOG.md's reservation note assumed elevation
                // might be needed before this tab existed; it is not, and F11 is the only entry
                // point.
                OpenPerformanceMonitor();   // PerformanceTabs.cs
                e.Handled = true;
            }
            // ── File operations (FileCommands.cs) ────────────────
            else if (e.Key == System.Windows.Input.Key.F2 && !ctrl && !alt)
            {
                RenameSelection();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Delete && !ctrl && !alt
                     && e.OriginalSource is not TextBox && e.OriginalSource is not ComboBox)
            {
                // Shift makes it permanent, exactly as in Explorer. Plain Delete recycles.
                DeleteSelection(permanent: shift);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.N)
            {
                NewFolderHere();
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.C
                     && e.OriginalSource is not TextBox)
            {
                CopySelection();
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.X
                     && e.OriginalSource is not TextBox)
            {
                CutSelection();
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.V
                     && e.OriginalSource is not TextBox)
            {
                PasteIntoCurrentFolder();
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.A
                     && e.OriginalSource is not TextBox)
            {
                // Select every row. Skipped inside a text box, where Ctrl+A has to keep meaning
                // "select this text".
                Pane.ResultsList.SelectAll();
                e.Handled = true;
            }
            else if (IsF10(e) && ctrl && !shift && !alt)
            {
                // Ctrl+F10: hide the pane menubar, in either pane (MenuBar.cs). It acts on the
                // folder LOCATION ROW, so it is a no-op on a tab kind that wears its own bar
                // instead - see SetLocationRow, which enforces that. It used to hand the row
                // back on top of a shell's or document's own bar, giving that tab two stacked
                // bars. Moved off plain F10 so F10 could become Dual
                // Pane; Shift+F10 (below) keeps meaning Windows' own context-menu key regardless.
                ToggleMenuBar();
                e.Handled = true;
            }
            else if (IsF10(e) && !ctrl && !shift && !alt)
            {
                // Plain F10: the second pane, or close it. Was F11 until the Processes tab
                // needed a home; handling the bare key also stops WPF
                // entering its native menu-activation mode, which is what F10 otherwise means to
                // a window. Ctrl+Shift+P stays as a legacy alias for anyone who learned it.
                ToggleDualPane();   // DualPane.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F11 && ctrl && !shift && !alt)
            {
                // Ctrl+F11: the Registry Editor tab, elevated - there is no bare-F11 variant for
                // this one; bare F11 stays the Performance tab (above) and is not repurposed.
                // Relaunches KillerShell through UAC and lands directly on a fresh Registry
                // Editor tab, the same "--registry" flag an unelevated tear-out already uses to
                // reopen the tab in a new window (TabTearOut.cs ApplyStartupTearOut), just
                // started with the runas verb instead of plainly (Elevation.cs
                // RelaunchElevatedRegistryEditor). Mirrors Ctrl+F12 for Event Viewer.
                RelaunchElevatedRegistryEditor();   // Elevation.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F12 && ctrl && !shift && !alt)
            {
                // Ctrl+F12: the Event Viewer tab, elevated - there is no bare-F12 variant, because
                // bare F12 is locked family-wide to the About card (below) and never repurposed.
                // Relaunches KillerShell through UAC and lands directly on a fresh Event Viewer
                // tab, the same "--eventviewer" flag an unelevated tear-out already uses to
                // reopen the tab in a new window (TabTearOut.cs ApplyStartupTearOut), just
                // started with the runas verb instead of plainly (Elevation.cs
                // RelaunchElevatedEventViewer). Mirrors F9 / Ctrl+F9 for Processes.
                RelaunchElevatedEventViewer();   // Elevation.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F12 && !ctrl && !shift && !alt)
            {
                // F12: the About card, same as KillerPDF. It was previously reachable only by
                // clicking the version in the footer, which nobody finds by accident. Explicitly
                // excludes Ctrl (above) so Ctrl+F12 does not ALSO open About - it used to, before
                // Event Viewer needed the chord.
                ShowAboutOverlay();   // About.cs
                e.Handled = true;
            }
            else if (ctrl && !shift && !alt && e.Key == System.Windows.Input.Key.E)
            {
                // Explorer's Ctrl+E puts the caret in the search box, so this does too.
                FocusSearchTerms();   // SearchPanel.cs
                e.Handled = true;
            }
            else if (ctrl && alt && !shift && e.Key == System.Windows.Input.Key.E)
            {
                // Ctrl+Alt+E: export as HTML. Export used to live on F9, which the Processes tab
                // needed - Ctrl+E and Ctrl+Shift+E were already taken
                // (search focus, exclude folder), so export moved here instead.
                Export_Click(this, new RoutedEventArgs());   // Export.cs - HTML
                e.Handled = true;
            }
            else if (ctrl && alt && shift && e.Key == System.Windows.Input.Key.E)
            {
                // Ctrl+Alt+Shift+E: the CSV variant, same reasoning as Ctrl+Alt+E above.
                ExportCsv_Click(this, new RoutedEventArgs());   // Export.cs - CSV
                e.Handled = true;
            }
            else if (ctrl && (e.Key == System.Windows.Input.Key.Right || e.Key == System.Windows.Input.Key.Left)
                     && !(e.OriginalSource is TextBox))
            {
                // Explicit expand / collapse - the toolbar button toggles, these don't.
                // Skipped inside a TextBox so Ctrl+arrow keeps its word-jump meaning there.
                bool expand = e.Key == System.Windows.Input.Key.Right;
                foreach (var r in _active.Results) r.IsExpanded = expand;
                SetExpandAllLabel(expand);   // Results.cs
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.L)
            {
                // Ctrl+L is the address bar in Explorer and in every browser, and this is a
                // shell now, so it goes there. Clear moved to Ctrl+Shift+L (AddressBar.cs).
                BeginEditAddress();
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.L)
            {
                Clear_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (ctrl && e.Key == System.Windows.Input.Key.T)
            {
                NewTab_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (ctrl && e.Key == System.Windows.Input.Key.W)
            {
                CloseActiveTab_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (ctrl && e.Key == System.Windows.Input.Key.Tab)
            {
                CycleTab(shift ? -1 : 1);   // Tabs.cs
                e.Handled = true;
            }
            else if (ctrl && e.Key >= System.Windows.Input.Key.D1 && e.Key <= System.Windows.Input.Key.D9)
            {
                JumpToTab(e.Key - System.Windows.Input.Key.D1 + 1);   // Tabs.cs; 9 = last
                e.Handled = true;
            }
            else if (ctrl && e.Key >= System.Windows.Input.Key.NumPad1 && e.Key <= System.Windows.Input.Key.NumPad9)
            {
                JumpToTab(e.Key - System.Windows.Input.Key.NumPad1 + 1);
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.N)
            {
                // Explorer's New Window, which is what every hand arriving here expects Ctrl+N
                // to be. It held "add a search term" only because there was no second window to
                // give it to; there is now, so the term moved to Ctrl+Shift+A as promised.
                OpenNewWindow();   // NewWindow.cs
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.A
                     && !ChordOwnedByFocus(KsScope.Processes, KsScope.Terminal))
            {
                // Add a search term. A for "add", and Ctrl+Shift because the panel it acts on is
                // optional now - this is not a chord you reach for while browsing.
                //
                // SCOPE-GATED: the same chord is "Run as administrator" on a focused Processes
                // row and Select all in a focused shell. This handler previews from the window
                // root, so without the gate its meaning would be decided here every time and
                // neither of those could ever fire. See ChordOwnedByFocus above.
                _active.Groups[_active.Groups.Count - 1].Terms.Add(new SearchTerm());
                e.Handled = true;
            }
            else if (shift && !ctrl && !alt && e.Key == System.Windows.Input.Key.F7)
            {
                // Add a filter. It was Ctrl+Shift+N until New Folder claimed that chord back for
                // Explorer, then bare F7 - which collided with the editor further down this same
                // chain. This branch is FIRST, so an unguarded F7 here swallowed the key and the
                // editor's branch never ran at all: F7 opened a filter row and nothing else,
                // while the card, the README and the results menu all promised it opened the
                // file. The editor keeps the bare key, because opening what you just found is
                // the more common thing to want and it has no other keyboard route; adding a
                // filter still has its own button in the search panel. Shift for the secondary,
                // as with Shift+F8 for CMD.
                AddFilter_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.O)
            {
                // !shift matters: without it this swallows Ctrl+Shift+O (Open with), because
                // this branch sits ahead of it in the chain.
                OpenFolderPicker();
                e.Handled = true;
            }
            // ── Shell (TerminalTabs.cs) ─────────────────────────
            // F8 is the primary key: opening a shell in the folder you are looking at is one of
            // the reasons to use this app, and a two-hand chord is the wrong price for it. It
            // took F8 from CSV export, which moved to Ctrl+Alt+Shift+E once F9 itself went to the
            // Processes tab.
            //
            // Shift picks CMD (the LCD skin), Ctrl asks for the elevated one - which relaunches
            // us through UAC (Elevation.cs) rather than opening a tab in this process.
            //
            // F8 IS in IsWindowChord (TerminalTabs.cs), so it opens a shell from inside a shell
            // too. It cost PSReadLine's F8 history search to do that, which was the argument for
            // the other way round - but a headline key that stops working once you are in the
            // thing it opens reads as broken, and history is still on Ctrl+r and prefix+Up.
            else if (!alt && e.Key == System.Windows.Input.Key.F8)
            {
                bool admin = ctrl;
                OpenShell(shift ? Terminal.TerminalProfile.Cmd(elevated: admin)
                                : Terminal.TerminalProfile.PowerShell(elevated: admin));
                e.Handled = true;
            }
            // Ctrl+` is the chord VS Code and Windows Terminal both use for "open a terminal
            // here", so a hand trained on either arrives expecting it. Kept as an alias for F8.
            else if (ctrl && !alt && e.Key == System.Windows.Input.Key.OemTilde)
            {
                OpenShell(shift ? Terminal.TerminalProfile.Cmd()
                                : Terminal.TerminalProfile.PowerShell());
                e.Handled = true;
            }
            else if (ctrl && alt && e.Key == System.Windows.Input.Key.OemTilde)
            {
                // Ctrl+Alt+` asks for the elevated one, which relaunches us through UAC
                // (Elevation.cs) rather than opening a tab here.
                OpenShell(shift ? Terminal.TerminalProfile.Cmd(elevated: true)
                                : Terminal.TerminalProfile.PowerShell(elevated: true));
                e.Handled = true;
            }
            // ── Results context-menu commands (ResultsMenu.cs) ───
            // Conventions first: where Windows or Explorer already owns a chord for one of
            // these, that chord wins, because a hand trained anywhere else arrives expecting it.
            // Alt+Enter (Properties), Shift+F10 (shell menu), F3 (search), Ctrl+D (favorite)
            // and Ctrl+Shift+C (copy as path) are all Windows'. Ctrl+Shift+Enter and plain Enter
            // live in the Enter branch below, where they share the key with running a search.
            //
            // The rest have no convention to inherit and carry provisional Ctrl+Shift chords so
            // that every menu row is reachable and shows up on the F1 card - those are the ones
            // to re-cut once the overlay makes the whole map visible.
            //
            // All of them go through FromKeyboard, never straight at the handler: see the note
            // on that method about the stale right-click seed.
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.P)
            {
                // Second pane. This was the PROVISIONAL chord before bare F10 took over dual
                // pane; kept as a legacy alias now that F10 is primary, for
                // anyone whose hand already learned it, and listed on the F10 row in KsAll
                // (ShortcutsOverlay.cs) rather than as a row of its own - it does light its own
                // keycap on the Ctrl+Shift layer, through that row's empty-Keys alias entry.
                // Right-clicking the toolbar button flips the orientation; that has no key yet
                // on purpose.
                ToggleDualPane();   // DualPane.cs
                e.Handled = true;
            }
            else if (IsF10(e) && shift)
            {
                FromKeyboard(MenuShell_Click);         // Windows' own context-menu key
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F3 && !ctrl && !shift && !alt)
            {
                FromKeyboard(MenuSearchHere_Click);    // Explorer's search key
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F6 && !ctrl && !shift && !alt)
            {
                FromKeyboard(MenuShowInExplorer_Click);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F7 && !ctrl && !shift && !alt)
            {
                // Edit the selected file in a tab. A bare F-key rather than a Ctrl chord because
                // that is the house style, and F7 was the one still free - F4, the file
                // manager's traditional edit key, has been the address bar here since before
                // there was an editor to give it to.
                //
                // With NOTHING selected the key opens a blank document instead of reporting that
                // it needs a file. MenuEdit_Click only ever acts on a selection, so an empty one
                // set a status line and returned, which from the outside looked like a dead key.
                // The menu row keeps that behavior - it is always seeded by what is under the
                // pointer, so it has a file by definition.
                if (Pane.ResultsList.SelectedItems.Count == 0) NewDocument();   // EditorTabs.cs
                else FromKeyboard(MenuEdit_Click);                              // ResultsMenu.cs
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F7 && ctrl && !shift && !alt)
            {
                // Ctrl+F7: the same blank document as plain F7, except THIS tab's save retries
                // elevated on an access-denied write instead of just failing (EditorTabs.cs
                // NewDocumentAdmin / SaveActiveEditor, Elevation.cs RetrySaveElevated).
                NewDocumentAdmin();   // EditorTabs.cs
                e.Handled = true;
            }
            else if (ctrl && !shift && e.Key == System.Windows.Input.Key.D)
            {
                FromKeyboard(MenuFavorite_Click);      // the browser bookmark chord
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.C
                     && !ChordOwnedByFocus(KsScope.Terminal))
            {
                // Windows 11's "Copy as path". This chord used to toggle case-sensitivity,
                // which moved to Alt+C - the convention has the stronger claim on it.
                //
                // SCOPE-GATED: a focused shell binds the same chord to Copy, which is the
                // Windows Terminal convention and the only way to copy a selection out of a pty
                // without stealing Ctrl+C (interrupt). Copying a file's path means nothing over
                // a terminal, so the window stands down there. See ChordOwnedByFocus above.
                FromKeyboard(MenuCopyPath_Click);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.O)
            {
                FromKeyboard(MenuOpenWith_Click);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.E)
            {
                FromKeyboard(MenuExcludeFolder_Click);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.M)
            {
                FromKeyboard(MenuCopyName_Click);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.D)
            {
                FromKeyboard(MenuCopyFolder_Click);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.Y)
            {
                FromKeyboard(MenuCopyLines_Click);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == System.Windows.Input.Key.H)
            {
                FromKeyboard(MenuCopyHash_Click);
                e.Handled = true;
            }
            else if (alt && !ctrl && !shift
                     && (e.Key == System.Windows.Input.Key.C
                         || (e.Key == System.Windows.Input.Key.System && e.SystemKey == System.Windows.Input.Key.C)))
            {
                // Case-sensitivity, moved off Ctrl+Shift+C so Windows' copy-as-path can have it.
                // Alt chords arrive as Key.System with the real key in SystemKey.
                CaseSensitiveCheck.IsChecked = CaseSensitiveCheck.IsChecked != true;
                e.Handled = true;
            }
            // Alt+Enter arrives as Key.System with Enter parked in SystemKey, exactly like the
            // Alt chords at the top of this method - so matching on e.Key alone would never see
            // it and Properties would silently do nothing.
            else if (e.Key == System.Windows.Input.Key.Enter
                     || (e.Key == System.Windows.Input.Key.System
                         && e.SystemKey == System.Windows.Input.Key.Enter))
            {
                // Let open dropdowns, the date box, and the filter box handle Enter
                // themselves, otherwise they can never commit.
                if (e.OriginalSource is ComboBoxItem ||
                    (e.OriginalSource is ComboBox cb && cb.IsDropDownOpen) ||
                    e.OriginalSource is System.Windows.Controls.Primitives.DatePickerTextBox ||
                    ReferenceEquals(e.OriginalSource, Pane.ResultFilterBox))
                    return;

                // Enter now has to serve two masters. In the results list with something
                // selected it OPENS, which is what Enter does in every file manager. Anywhere
                // else - the search panel, an empty list, no selection - it still runs the
                // search. Ctrl+Shift+Enter opens elevated, the Start menu's chord.
                if (ResultsListHasFocus() && Pane.ResultsList.SelectedItems.Count > 0)
                {
                    if (ctrl && shift) FromKeyboard(MenuOpenAdmin_Click);
                    else if (alt)      FromKeyboard(MenuProperties_Click);
                    else               FromKeyboard(MenuOpen_Click);
                    e.Handled = true;
                    return;
                }

                Search_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                // Smart Esc, in order: close the filter bar > close an open overlay >
                // stop a running search > offer to quit (with remember-my-choice).
                e.Handled = true;
                if (Pane.ResultFilterBar.Visibility == Visibility.Visible)
                    ResultFilterClose_Click(this, new RoutedEventArgs());
                else if (PatternHelpOverlay.Visibility == Visibility.Visible)
                    PatternHelpClose_Click(this, new RoutedEventArgs());
                else if (ShortcutsOverlay.Visibility == Visibility.Visible)
                    ShortcutsClose_Click(this, new RoutedEventArgs());
                else if (AboutOverlay.Visibility == Visibility.Visible)
                    AboutClose_Click(this, new RoutedEventArgs());
                else if (_active.IsSearching)
                    _active.Cts?.Cancel();
                else
                    RequestQuit();   // Session.cs
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  ENGINE CALLBACKS  (marshalled to the UI thread, routed per tab)
        // ═══════════════════════════════════════════════════════════
        private void SetTabStatus(SearchTab tab, string msg)
        {
            tab.StatusKey     = null;   // transient text - not re-renderable on language switch
            tab.StatusArgs    = null;
            tab.StatusMessage = msg;
            if (tab == _active) { SetFooterStatus(msg); ApplyStatusTone(null); }
        }

        // ── Footer status line: HEAD elision ─────────────────────────────────
        // The live line is usually a path, and a path is only useful from the RIGHT: the file
        // name and extension are the part being read. WPF's TextTrimming only ellipsizes the
        // TAIL, which throws away exactly that. So the TextBlock carries no TextTrimming and
        // this walks the string in from the FRONT instead - "...\lodash.sortby\README.md" -
        // dropping the long folder names and keeping the file name.
        //
        // The width budget is StatusText's own ActualWidth, which IS its star column: with the
        // portable badge shown, that column stops at the badge; once the app is installed the
        // badge collapses, its Auto column goes to zero width, and the line simply gets that
        // space and runs on toward the version text. No special case needed for either.
        //
        // Every caller goes through here rather than touching StatusText.Text, or the stored
        // full string and what is on screen drift apart on the next resize.
        private string _statusFull = string.Empty;

        private void SetFooterStatus(string msg)
        {
            _statusFull = msg ?? string.Empty;
            ElideFooterStatus();
        }

        // Ellipsis built from its codepoint, never typed literally: these sources are BOM-less
        // UTF-8 and the family keeps them 0 non-ASCII bytes (the encoding trap that made
        // KillerPDF's release.ps1 PS7-only).
        private static readonly string Ellipsis = ((char)0x2026).ToString();

        private void ElideFooterStatus()
        {
            // Nothing pushed yet - leave the XAML's Str_Status_Ready alone rather than
            // blanking it on the first layout pass.
            if (_statusFull.Length == 0) return;

            // The star column runs UNDER the centered portable badge, so ActualWidth alone is
            // not the real budget - that is what let the line reach the Install button. While
            // the badge is showing, clamp at its left edge with a 12px gap. Once the app is
            // installed the badge is collapsed, the clamp drops, and the line is free to run on
            // toward the version text.
            double avail = StatusText.ActualWidth;
            if (PortableBadge.IsVisible && PortableBadge.ActualWidth > 0)
            {
                double badgeLeft = PortableBadge
                    .TransformToVisual(StatusText).Transform(new Point(0, 0)).X;
                avail = Math.Min(avail, badgeLeft - 12);
            }

            if (avail <= 0)                          { StatusText.Text = _statusFull; return; }
            if (MeasureStatus(_statusFull) <= avail) { StatusText.Text = _statusFull; return; }

            // Longest SUFFIX that still fits behind a leading ellipsis. Binary search rather
            // than a character-at-a-time walk - this runs on every engine progress callback.
            int lo = 0, hi = _statusFull.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;   // drop `mid` characters off the front
                if (MeasureStatus(Ellipsis + _statusFull.Substring(mid)) <= avail) hi = mid;
                else lo = mid + 1;
            }
            StatusText.Text = Ellipsis + _statusFull.Substring(Math.Min(lo, _statusFull.Length));
        }

        private void StatusText_SizeChanged(object sender, SizeChangedEventArgs e) => ElideFooterStatus();

        // The pane's status bar earns its 24px only when it has something to say. Browsing a
        // folder leaves all three of its fields empty - the scanned and match counts belong to
        // a search and the query label is blank - so the row is dead space under the listing.
        // Collapse it then, and hand the pane's bottom rounding back to the list surface, which
        // is square only because the bar normally covers that edge.
        //
        // Called from every place that writes those three fields; cheap enough to sit on the
        // progress callback.
        private void UpdatePaneStatusBar()
        {
            // This bar belongs to the FILE BROWSER - item counts, the search query, "No item
            // selected". A terminal, an editor or a tool tab has nothing to say through it, and
            // it was showing over all of them: a shell tab carried a stray "No item selected"
            // strip along its bottom edge.
            //
            // The test is whether the LISTING is on screen, not what kind the tab claims to be.
            // Keying off IsBrowsing/IsSearching did not work and is why this came back twice: a
            // shell tab is opened from a folder and keeps IsBrowsing true the whole time it is a
            // terminal, so the guard passed and the strip stayed. Every non-listing kind collapses
            // ResultsList on activation (TerminalTabs, EditorTabs, ProcessTabs, EventViewerTabs,
            // PerformanceTabs, RegistryEditorTabs) and all of those run before this in ActivateTab,
            // so reading the result of that cannot drift from it. ResultsView.cs makes the same
            // test for the same reason.
            bool browsing = Pane.ResultsList.Visibility == Visibility.Visible;

            bool any = browsing
                    && (Pane.ScannedText.Visibility == Visibility.Visible
                        || !string.IsNullOrEmpty(Pane.StatsText.Text)
                        || !string.IsNullOrEmpty(Pane.QueryText.Text));

            Pane.PaneStatusBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

            // Hands the pane's bottom rounding back to the list surface when the bar is gone. The
            // 5 was a literal and kept two rounded corners on the flat theme; PaneRadius is 0 there.
            double br = any ? 0 : PaneRadius;
            var radius = new CornerRadius(0, 0, br, br);
            Pane.ResultsSurface.CornerRadius      = radius;
            Pane.ResultsSurfaceGrain.CornerRadius = radius;
        }

        // The badge appearing or going away changes how much room the line has, and it does NOT
        // resize StatusText to say so - the star column is the same width either way, the badge
        // just stops sitting on top of it. Dispatched so the badge has been arranged and has a
        // real ActualWidth by the time the clamp reads it.
        private void PortableBadge_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
            => Dispatcher.BeginInvoke(new Action(ElideFooterStatus),
                                      System.Windows.Threading.DispatcherPriority.Loaded);

        // Measured with a real off-tree TextBlock, NOT FormattedText. FormattedText measures in
        // TextFormattingMode.Ideal and offers no way to ask for Display; this window is
        // Display (see the Window's TextOptions), which snaps every glyph advance to a whole
        // pixel and so lays out WIDER than Ideal predicts. Measuring in the wrong mode
        // under-reports by a few percent, which is enough for a long path to run into the
        // Install button anyway - which is exactly what it did.
        //
        // Off-tree so measuring never touches the live layout pass; the font properties and the
        // formatting mode are copied each call because both ride DynamicResources that a theme
        // or locale switch can change underneath us.
        private TextBlock? _statusMeasure;

        private double MeasureStatus(string s)
        {
            _statusMeasure ??= new TextBlock();
            _statusMeasure.FontFamily  = StatusText.FontFamily;
            _statusMeasure.FontSize    = StatusText.FontSize;
            _statusMeasure.FontStyle   = StatusText.FontStyle;
            _statusMeasure.FontWeight  = StatusText.FontWeight;
            _statusMeasure.FontStretch = StatusText.FontStretch;
            System.Windows.Media.TextOptions.SetTextFormattingMode(
                _statusMeasure, System.Windows.Media.TextOptions.GetTextFormattingMode(StatusText));
            _statusMeasure.Text = s;
            _statusMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return _statusMeasure.DesiredSize.Width;
        }

        // The footer indicator light. Green normal, amber for something that did not happen but
        // was not a fault, red for a genuine failure.
        //
        // Driven off the status KEY rather than a separate argument at every call site: the key
        // already says which of those three a message is, and threading a tone through forty
        // SetTabStatusKey calls would be forty chances to get it wrong. A raw SetTabStatus has no
        // key and is always green - those are progress messages.
        private static readonly string[] WarnKeys =
        {
            "Str_Status_FileOnly", "Str_Status_ClipboardBusy", "Str_Status_ElevationDeclined",
        };

        private static readonly string[] ErrorKeys =
        {
            "Str_Status_BadPath", "Str_Status_ShellFailed",
        };

        private void ApplyStatusTone(string? key)
        {
            // A real traffic light: three fixed colors. This used to fall back to PrimaryBrush,
            // which meant "fine" rendered as whatever accent was picked - blue, red, whatever -
            // so the dot carried no information at all unless something had gone wrong.
            //
            // A run in flight holds it amber for the whole search, so the light reads as
            // "working" rather than "fine" while it is still going. An error KEY still wins over
            // that: something failing mid-run must not be downgraded to merely busy.
            string brush = key != null && System.Array.IndexOf(ErrorKeys, key) >= 0 ? "DangerRed"
                         : _active?.IsSearching == true                             ? "WarnBrush"
                         : key != null && System.Array.IndexOf(WarnKeys,  key) >= 0 ? "WarnBrush"
                         : "OkBrush";

            StatusDot.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, brush);
        }

        // Key-based variant: stores the resource key + args on the tab so a live
        // language switch can re-render every tab's status line (RelocalizeDynamicUi).
        private void SetTabStatusKey(SearchTab tab, string key, params object[] args)
        {
            tab.StatusKey     = key;
            tab.StatusArgs    = args;
            tab.StatusMessage = args.Length > 0 ? string.Format(Loc(key), args) : Loc(key);
            if (tab == _active) { SetFooterStatus(tab.StatusMessage); ApplyStatusTone(key); }
        }

        // All engine callbacks land at Background priority so queued result churn sits
        // behind input. Priority alone is NOT what keeps the window alive though: it only
        // orders work that is still queued, and a callback already running cannot be
        // interrupted. Responsiveness comes from the engine capping each batch (SearchEngine.cs
        // MaxBatch), so every callback is short and input gets a slot between them. Do not
        // remove that cap on the assumption this priority is covering it.
        private void OnResultsBatch(SearchTab tab, List<SearchResult> batch)
        {
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var result in batch)
                {
                    tab.Results.Add(result);
                    foreach (var m in result.Matches)
                    {
                        if (m.Term.MatchCount < 0) m.Term.MatchCount = 0;
                        m.Term.MatchCount++;
                    }
                }
                int c = tab.Results.Count;
                tab.StatsLabel = c > 0 ? string.Format(Loc("Str_Count_Matches"), c.ToString("N0")) : string.Empty;
                if (tab == _active) { Pane.StatsText.Text = tab.StatsLabel; UpdatePaneStatusBar(); }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnStatusChanged(SearchTab tab, string status)
            => Dispatcher.InvokeAsync(() => SetTabStatus(tab, status),
                System.Windows.Threading.DispatcherPriority.Background);

        private void OnProgressChanged(SearchTab tab, int processed)
            => Dispatcher.InvokeAsync(() =>
            {
                tab.ScannedCount = processed;
                tab.ScannedLabel = string.Format(Loc("Str_Status_Scanned"), processed.ToString("N0"));
                if (tab == _active) { Pane.ScannedText.Text = tab.ScannedLabel; UpdatePaneStatusBar(); }
            }, System.Windows.Threading.DispatcherPriority.Background);

        private void SetStatus(string msg) => SetTabStatus(_active, msg);
    }
}
