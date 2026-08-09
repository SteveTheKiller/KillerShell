using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KillerShell.Services;

// KillerUI / Grunge - title-bar theme + accent pickers. Partial of MainWindow.
// Ported verbatim from KillerPDF's RailFlyouts.cs/SettingsPanel.cs pattern (Steve, 2026-08-02:
// "COPY THE EXACT MENU FROM KILLERPDF FOR EVERYTHING"): ThemeFlyout is a Button.ContextMenu with
// FlyoutCard/FlyoutGrain chrome (MainWindow.xaml), opened the same way LangMenu already was -
// which is what proved the positioning actually works, since Steve confirmed the language picker
// landed in the right place while the old Popup-based ThemeFlyout did not.
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
        /// under the frozen picture, then fade the picture out over the repainted UI (Steve,
        /// 2026-08-09: "theme changes are super slow now. cant we crossfade"). The same
        /// snapshot-and-fade shape TabFadeGhost uses for tab switches. Best-effort: if the
        /// snapshot fails for any reason the swap just runs bare, exactly as before.
        /// </summary>
        private void CrossfadeSwap(Action swap)
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

            swap();

            if (ghost == null) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var g = ghost;
            fade.Completed += (_, _) => RootGrid.Children.Remove(g);
            // Deferred one dispatcher pass so the new theme has actually PAINTED under the
            // ghost before it starts to lift - fading over a half-repainted frame is the
            // flicker this exists to hide.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => g.BeginAnimation(OpacityProperty, fade)));
        }

        private void SelectTheme(Theme theme)
        {
            bool wasOpen = ThemeFlyout is not null && ThemeFlyout.IsOpen;
            CrossfadeSwap(() => ThemeManager.Apply(theme));
            ApplyThemeBorder(this);   // retint the DWM frame border to the new palette
            // Corner preference is owned here too: 98SE squares even a floating window, so
            // switching INTO or OUT OF a flat theme has to re-evaluate it, not just a state
            // change. Same call KillerNotes makes from its own theme switch.
            ApplyWindowCorners(this, rounded: WindowState == WindowState.Normal);
            // Radios are already synced - the user's own click just set this one and WPF's
            // GroupName handles unchecking the rest. Dot rings + the pop-out slide still need
            // driving; UpdateAccentRowsVisibility(animate: true) is the one that runs with no
            // animation on flyout open instead (ThemeButton_Click below).
            UpdateAccentSwatches();
            UpdateAccentRowsVisibility(animate: true);

            // A shell resolves its colors ONCE, when its palette is built - it has to, since
            // it paints thousands of cells a frame and cannot carry a DynamicResource per
            // cell. So a theme switch has to tell it to rebuild, or every open terminal
            // keeps the colors of the theme it was opened under (TerminalTabs.cs).
            RefreshTerminalThemes();

            // An open document resolves its colors the same way and for the same reason
            // (EditorTabs.cs, Editing/EditorControl.ApplyTheme).
            RefreshEditorThemes();

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
            CrossfadeSwap(() => ThemeManager.ApplyAccent(family, accent));
            UpdateAccentSwatches();
            RefreshTerminalThemes();
            RefreshEditorThemes();
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
