using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KillerShell.Services;

// KillerUI / Grunge - title-bar theme + accent pickers. Partial of MainWindow.
// Ported verbatim from KillerPDF's RailFlyouts.cs/SettingsPanel.cs pattern: ThemeFlyout is a
// Button.ContextMenu with FlyoutCard/FlyoutGrain chrome (MainWindow.xaml), opened the same way
// LangMenu already was - which is what proved the positioning actually works, since the
// language picker landed in the right place while the old Popup-based ThemeFlyout did not.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private void ThemeDarkRadio_Checked(object sender, RoutedEventArgs e)      => SelectTheme(Theme.Dark);
        private void ThemeLightRadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.Light);
        private void ThemeHCRadio_Checked(object sender, RoutedEventArgs e)        => SelectTheme(Theme.Black);
        private void Theme98SERadio_Checked(object sender, RoutedEventArgs e)      => SelectTheme(Theme.SE98);
        private void ThemeBloodRadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.Blood);
        private void ThemeGreedRadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.Greed);
        private void ThemeCyanoticRadio_Checked(object sender, RoutedEventArgs e)  => SelectTheme(Theme.Cyanotic);
        private void ThemeEctoplasmRadio_Checked(object sender, RoutedEventArgs e) => SelectTheme(Theme.Ectoplasm);
        private void ThemeDecayRadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.Decay);
        private void ThemeMourningRadio_Checked(object sender, RoutedEventArgs e)  => SelectTheme(Theme.Mourning);
        private void ThemeSepulchreRadio_Checked(object sender, RoutedEventArgs e) => SelectTheme(Theme.Sepulchre);
        private void ThemeDeliriumRadio_Checked(object sender, RoutedEventArgs e)  => SelectTheme(Theme.Delirium);
        private void ThemeMalaiseRadio_Checked(object sender, RoutedEventArgs e)   => SelectTheme(Theme.Malaise);

        /// <summary>
        /// Crossfade a theme/accent swap: snapshot the window as it looks NOW, run the swap
        /// under the frozen picture, then fade the picture out over the repainted UI, so a
        /// slow theme change reads as one smooth transition. The same snapshot-and-fade shape
        /// TabFadeGhost uses for tab switches. Best-effort: if the snapshot fails for any
        /// reason the swap just runs bare, exactly as before.
        /// The optional HEAVY work (terminal/editor recolor) runs under the fully opaque
        /// ghost, and the fade only starts once it has run AND painted (the fade was
        /// starting at Loaded priority while the heavy refreshes sat queued at Background,
        /// which runs LATER, so they snapped in mid-fade).
        /// The optional FRAME work is anything that repaints the window's OUTER edge. It is
        /// held back and run on the fade's own clock rather than inline with the swap - see
        /// the comment on the parameter below for why a ghost cannot solve this one.
        /// </summary>
        /// <param name="frame">
        /// Work that recolors the window FRAME, run at the instant the fade starts.
        ///
        /// The window ghost covers RootGrid, and the theme flyout gets a second ghost of its own
        /// (below) because a ContextMenu is a separate top-level HWND that the window snapshot can
        /// neither include nor cover. The frame is a THIRD surface with the same problem and no
        /// ghost can fix it: on the twelve rounded themes the only frame the user sees is the 1px
        /// DWM window border, painted by the compositor in the NON-CLIENT area from
        /// DWMWA_BORDER_COLOR (Chrome.cs ApplyThemeBorder). Nothing added to the WPF visual tree
        /// is over it, because it is not inside the window's client area at all. (The in-tree
        /// frame borders in MainWindow.xaml - WindowFrame, FrameOuter*, FrameInner* - are
        /// zero-thickness on every theme but 98SE, where WindowFramePadding is 0 as well, so on
        /// those twelve there is nothing of them to cover either way.)
        ///
        /// So the frame is held to the OLD color instead of being covered, and switched on the
        /// same clock as the fade. Before this, ApplyThemeBorder was called inline right after
        /// CrossfadeSwap returned - which is immediately, since all CrossfadeSwap does is queue -
        /// so the border snapped to the new accent while every pixel inside it sat frozen on the
        /// stale snapshot. That mismatch is what made the wait before the fade read as a hang
        /// rather than as a transition: the window had visibly already changed, and then stopped.
        /// </param>
        private void CrossfadeSwap(Action swap, Action? heavy = null, Action? frame = null)
        {
            System.Windows.Controls.Image? ghost = null;
            try
            {
                if (ActualWidth > 0 && ActualHeight > 0)
                {
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight),
                        96, 96, PixelFormats.Pbgra32);
                    rtb.Render(this);
                    rtb.Freeze();
                    ghost = new System.Windows.Controls.Image
                    { Source = rtb, Stretch = Stretch.Fill, IsHitTestVisible = false };
                    Grid.SetRowSpan(ghost, 3);
                    System.Windows.Controls.Panel.SetZIndex(ghost, 9500);
                    RootGrid.Children.Add(ghost);
                }
            }
            catch { ghost = null; }

            // The theme flyout is a ContextMenu - a Popup with its own top-level HWND, so the
            // window ghost neither INCLUDES it (rtb.Render(this) renders only the window's
            // tree) nor COVERS it (the popup floats above everything in RootGrid). Left alone
            // it recolored the instant the dictionaries swapped while the rest of the window
            // sat frozen under the ghost. So the open flyout gets its OWN ghost,
            // injected into its template root Grid and faded on the same clock as the
            // window's. Explicit size + top-left alignment, not Stretch: the accent-row
            // slide can change the menu's height under the ghost, and a stretched Image
            // would distort with it. Same best-effort rule as the window snapshot.
            Grid? menuRoot = null;
            System.Windows.Controls.Image? menuGhost = null;
            try
            {
                if (ThemeFlyout is { IsOpen: true }
                    && System.Windows.Media.VisualTreeHelper.GetChildrenCount(ThemeFlyout) > 0
                    && System.Windows.Media.VisualTreeHelper.GetChild(ThemeFlyout, 0) is Grid mroot
                    && mroot.ActualWidth > 0 && mroot.ActualHeight > 0)
                {
                    var mrtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(mroot.ActualWidth), (int)Math.Ceiling(mroot.ActualHeight),
                        96, 96, PixelFormats.Pbgra32);
                    mrtb.Render(mroot);
                    mrtb.Freeze();
                    menuGhost = new System.Windows.Controls.Image
                    {
                        Source = mrtb, Stretch = Stretch.None, IsHitTestVisible = false,
                        Width = mroot.ActualWidth, Height = mroot.ActualHeight,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    System.Windows.Controls.Panel.SetZIndex(menuGhost, 9500);
                    mroot.Children.Add(menuGhost);
                    menuRoot = mroot;
                }
            }
            catch { menuRoot = null; menuGhost = null; }

            swap();

            if (ghost == null)
            {
                if (menuRoot != null && menuGhost != null) menuRoot.Children.Remove(menuGhost);
                TimedStep("heavy", heavy);
                // No ghost means no fade and so no clock to hold the frame back to: the swap is
                // already visible, and delaying the border by a dispatcher turn here would create
                // the very mismatch the deferral exists to remove.
                frame?.Invoke();
                return;
            }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var g = ghost;
            fade.Completed += (_, _) => RootGrid.Children.Remove(g);
            var mg = menuGhost; var mr = menuRoot;
            // Ordering is the whole trick. This outer callback is queued at Background so it
            // lands BEHIND every Background-deferred refresh the swap itself queued (the
            // ThemeChanged handler's RepaintIcons - dispatcher order is FIFO within one
            // priority). The heavy work then runs here, still under the opaque ghost. The
            // fade is queued LAST, at Loaded - Render priority sits above Loaded, so the
            // recolored frame is guaranteed to have painted under the ghost before it starts
            // to lift. Fading over a half-repainted frame is the flicker this exists to hide.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                (Action)(() =>
                {
                    TimedStep("heavy", heavy);
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        (Action)(() =>
                        {
                            // The frame goes FIRST and in this same dispatcher slot, so the
                            // window's edge and its interior start changing on one clock. Fade
                            // START rather than fade completion: the start is the instant the
                            // whole window visibly begins to turn over, so the border joining it
                            // there reads as part of the same transition. Held to the end it
                            // would just move the mismatch to the other side of the fade.
                            frame?.Invoke();
                            g.BeginAnimation(OpacityProperty, fade);
                            if (mg != null && mr != null)
                            {
                                // Its own animation instance, same parameters: an Animation
                                // already started on one target cannot be reused on another.
                                var mfade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
                                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                                mfade.Completed += (_, _) => mr.Children.Remove(mg);
                                mg.BeginAnimation(OpacityProperty, mfade);
                            }
                        }));
                }));
        }

        /// <summary>
        /// Run <paramref name="work"/>, and in a DEBUG build print how long it took as
        /// "[theme] &lt;label&gt;: N.N ms" in the debug output. Null work is a no-op and prints
        /// nothing.
        /// </summary>
        /// <remarks>
        /// A theme switch shows the ghost, pauses, then fades. The pause is whatever runs between
        /// the swap and the fade, and there are two candidates sitting in that gap: the
        /// terminal/editor recolor handed to CrossfadeSwap as its heavy work, and the RepaintIcons
        /// the ThemeChanged handler defers to Background priority (MainWindow.xaml.cs). Which of
        /// them dominates is not obvious from reading either one, and guessing is how the wrong
        /// half gets optimized - so both are timed through here and a single theme click prints
        /// the two numbers next to each other.
        ///
        /// Note that neither number accounts for the whole pause. Swapping the palette invalidates
        /// ~150 resource keys across the entire visual tree, and the measure/arrange/render pass
        /// that follows runs at Render priority, ABOVE the Background slot these two occupy, so it
        /// is already spent before either stopwatch starts. If both print near zero, that layout
        /// pass is the remainder and nothing in this file can shorten it.
        ///
        /// The Stopwatch is unconditional while the REPORT is [Conditional("DEBUG")], not the
        /// other way round: a conditional call has its argument expressions compiled out along
        /// with it, so a release build carries no string formatting and no Debug.WriteLine - only
        /// one Stopwatch whose result is never read, which is cheaper than maintaining two code
        /// paths for the same call site.
        /// </remarks>
        private static void TimedStep(string label, Action? work)
        {
            if (work == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            work();
            sw.Stop();
            ReportStep(label, sw.Elapsed.TotalMilliseconds);
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void ReportStep(string label, double ms)
            => System.Diagnostics.Debug.WriteLine("[theme] " + label + ": "
                + ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms");

        private void SelectTheme(Theme theme)
        {
            bool wasOpen = ThemeFlyout is not null && ThemeFlyout.IsOpen;
            // A shell resolves its colors ONCE, when its palette is built - it has to, since
            // it paints thousands of cells a frame and cannot carry a DynamicResource per
            // cell. So a theme switch has to tell it to rebuild, or every open terminal
            // keeps the colors of the theme it was opened under (TerminalTabs.cs). Passed as
            // CrossfadeSwap's HEAVY work so it runs under the opaque ghost and the fade only
            // starts after it has painted - deferring it at Background priority on its own
            // put it AFTER the fade's start and it snapped in mid-fade.
            //
            // ApplyThemeBorder retints the DWM frame border to the new palette. It is passed as
            // CrossfadeSwap's FRAME work rather than called here, because "here" is the moment
            // CrossfadeSwap returns and CrossfadeSwap only QUEUES - so calling it inline flipped
            // the window's outline to the new accent while the whole interior was still frozen on
            // the ghost, a second before the fade started. The border is non-client, painted by
            // DWM outside the WPF tree, so no ghost can cover it; holding it to the fade's clock
            // is the only way it changes with everything else.
            CrossfadeSwap(() => ThemeManager.Apply(theme),
                          () => { RefreshTerminalThemes(); RefreshEditorThemes(); },
                          () => ApplyThemeBorder(this));
            // Corner preference is owned here too: 98SE squares even a floating window, so
            // switching INTO or OUT OF a flat theme has to re-evaluate it, not just a state
            // change. Same call KillerNotes makes from its own theme switch.
            // Deliberately NOT deferred alongside the border above: this changes the window's
            // SHAPE, not a color, and only ever moves on a switch into or out of a flat theme -
            // and the shape has to be settled before the ghost is faded over it, or the corner
            // the DWM clips away changes underneath a picture that already has it drawn in.
            ApplyWindowCorners(this, rounded: WindowState == WindowState.Normal);
            // Radios are already synced - the user's own click just set this one and WPF's
            // GroupName handles unchecking the rest. Dot rings + the pop-out slide still need
            // driving; UpdateAccentRowsVisibility(animate: true) is the one that runs with no
            // animation on flyout open instead (ThemeButton_Click below).
            UpdateAccentSwatches();
            UpdateAccentRowsVisibility(animate: true);

            // Intentionally leave the flyout open so the user can try another theme right away,
            // same as PDF - a theme swap's side effects can knock the popup closed behind our
            // back, so check once layout settles and quietly reopen it in place if that happened.
            if (wasOpen)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() => { if (ThemeFlyout is not null && !ThemeFlyout.IsOpen) ThemeFlyout.IsOpen = true; }));
        }

        // Each theme family has its own accent-dot row, remembered independently
        // (ThemeManager.AccentChoiceFor). Clicking a dot sets that family's accent.
        private void AccentDot_Click(object sender, MouseButtonEventArgs e)      => HandleAccentDot(sender, Theme.Dark);
        private void AccentDotLight_Click(object sender, MouseButtonEventArgs e) => HandleAccentDot(sender, Theme.Light);
        private void AccentDotBlack_Click(object sender, MouseButtonEventArgs e) => HandleAccentDot(sender, Theme.Black);
        private void AccentDot98SE_Click(object sender, MouseButtonEventArgs e)  => HandleAccentDot(sender, Theme.SE98);

        private void HandleAccentDot(object sender, Theme family)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
            if (!Enum.TryParse<Accent>(tag, out var accent)) return;
            CrossfadeSwap(() => ThemeManager.ApplyAccent(family, accent),
                          () => { RefreshTerminalThemes(); RefreshEditorThemes(); });
            UpdateAccentSwatches();
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateThemeSwatchSelection();               // radios + accent dots to live state
            UpdateAccentRowsVisibility(animate: false);  // snap to state, no slide on open
            ToggleRailFlyout(ThemeFlyout);
        }

        private void UpdateThemeSwatchSelection()
        {
            var cur = ThemeManager.Current;
            ThemeDarkRadio.IsChecked      = cur == Theme.Dark;
            ThemeLightRadio.IsChecked     = cur == Theme.Light;
            ThemeHCRadio.IsChecked        = cur == Theme.Black;
            Theme98SERadio.IsChecked      = cur == Theme.SE98;
            ThemeBloodRadio.IsChecked     = cur == Theme.Blood;
            ThemeGreedRadio.IsChecked     = cur == Theme.Greed;
            ThemeCyanoticRadio.IsChecked  = cur == Theme.Cyanotic;
            ThemeEctoplasmRadio.IsChecked = cur == Theme.Ectoplasm;
            ThemeDecayRadio.IsChecked     = cur == Theme.Decay;
            ThemeMourningRadio.IsChecked  = cur == Theme.Mourning;
            ThemeSepulchreRadio.IsChecked = cur == Theme.Sepulchre;
            ThemeDeliriumRadio.IsChecked  = cur == Theme.Delirium;
            ThemeMalaiseRadio.IsChecked   = cur == Theme.Malaise;
            UpdateAccentSwatches();
        }

        // Slides the accent-dot picker to sit under whichever of Dark/Light/Black is active.
        // Ported verbatim from KillerPDF's SettingsPanel.cs UpdateAccentRowsVisibility/SlideRow:
        // each row animates its own Height, and because the outgoing row shrinks by the same
        // amount the incoming one grows (both AccentRowHeight, both AccentRowSlideMs, both
        // linear), the combined height stays constant - the picker slides to the new theme
        // instead of the whole flyout popping or resizing.
        private void UpdateAccentRowsVisibility(bool animate)
        {
            var cur = ThemeManager.Current;
            SlideRow(DarkAccentRow,  cur == Theme.Dark,  animate);
            SlideRow(LightAccentRow, cur == Theme.Light, animate);
            SlideRow(BlackAccentRow, cur == Theme.Black, animate);
            SlideRow(SE98AccentRow,  cur == Theme.SE98,  animate);
        }

        private const double AccentRowHeight = 26;   // 18px dot + 8px breathing room
        // Expand and collapse MUST share one duration (and stay linear): when switching between
        // neutral themes one row opens while another closes, and equal linear durations keep
        // their heights summing to a constant, so the popup's total height never dips/jumps
        // mid-animation - which is what would force a Popup resize/reposition mid-slide.
        private const double AccentRowSlideMs = 160;

        private static void SlideRow(FrameworkElement row, bool show, bool animate)
        {
            if (row is null) return;
            row.BeginAnimation(HeightProperty, null);   // drop any leftover/held animation
            if (show)
            {
                row.Visibility = Visibility.Visible;
                if (animate)
                {
                    row.Height = 0;
                    row.BeginAnimation(HeightProperty,
                        new DoubleAnimation(0, AccentRowHeight, TimeSpan.FromMilliseconds(AccentRowSlideMs)));
                }
                else row.Height = AccentRowHeight;
            }
            else if (animate && row.Visibility == Visibility.Visible && row.ActualHeight > 0.5)
            {
                var h = new DoubleAnimation(AccentRowHeight, 0, TimeSpan.FromMilliseconds(AccentRowSlideMs));
                h.Completed += (_, __) => { row.BeginAnimation(HeightProperty, null); row.Height = 0; row.Visibility = Visibility.Collapsed; };
                row.BeginAnimation(HeightProperty, h);
            }
            else
            {
                row.Height = 0;
                row.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateAccentSwatches()
        {
            var ring = TryFindResource("TextBrush") as Brush ?? Brushes.White;
            void RingRow(Border[] dots, Accent chosen)
            {
                foreach (var dot in dots)
                {
                    bool sel = dot.Tag is string t && Enum.TryParse<Accent>(t, out var a) && a == chosen;
                    dot.BorderBrush = sel ? ring : Brushes.Transparent;
                }
            }
            RingRow([AccentDotRed, AccentDotOrange, AccentDotGreen, AccentDotTeal, AccentDotBlue, AccentDotPurple], ThemeManager.AccentChoiceFor(Theme.Dark));
            RingRow([AccentDotLightRed, AccentDotLightOrange, AccentDotLightGreen, AccentDotLightTeal, AccentDotLightBlue, AccentDotLightPurple], ThemeManager.AccentChoiceFor(Theme.Light));
            RingRow([AccentDotBlackRed, AccentDotBlackOrange, AccentDotBlackGreen, AccentDotBlackTeal, AccentDotBlackBlue, AccentDotBlackPurple], ThemeManager.AccentChoiceFor(Theme.Black));
            RingRow([AccentDot98SERed, AccentDot98SEOrange, AccentDot98SEGreen, AccentDot98SETeal, AccentDot98SEBlue, AccentDot98SEPurple], ThemeManager.AccentChoiceFor(Theme.SE98));
        }

        /// <summary>
        /// Shared open/close for the rail's ContextMenu flyouts (Theme, Language) - PDF's
        /// RailFlyouts.cs ToggleRailFlyout, verbatim. The content pane bounds the window, the
        /// footer and the rail at once, so its bottom-left corner is the one spot a flyout can
        /// hug without covering any of them.
        /// </summary>
        private void ToggleRailFlyout(ContextMenu menu)
        {
            if (menu.IsOpen) { menu.IsOpen = false; return; }

            FlyoutPlacement.UsePane(PaneHost);
            FlyoutPlacement.Attach(menu, this);

            menu.IsOpen = true;
            menu.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }
}
