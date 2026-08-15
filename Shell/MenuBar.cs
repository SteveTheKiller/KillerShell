using System;
using System.Windows;
using System.Windows.Media.Animation;

// Hiding the pane menubar. Partial of MainWindow.
//
// Ctrl+F10 collapses the location row - nav buttons, path, tool strip - in the FOCUSED pane, and
// brings it back. Per pane, not window-wide: the two panes are usually doing different jobs, and
// a shell you want bare does not mean you also want the folder listing beside it stripped of its
// path and its view buttons.
//
// The row belongs to the PANE rather than to the tab, so every tab in that pane shares the
// state. Per tab would mean the chrome jumped on every tab switch, which is the opposite of
// what hiding it is for.
//
// Bare F10 toggles the second pane (dual pane) instead, since 2026-08-02 - see MainWindow.xaml.cs
// for that handler and DualPane.cs for what it does. Menu bar moved to Ctrl+F10 to sit next to it
// on the same key. Shift+F10 keeps its own meaning as Windows' context-menu key. Both F10 forms
// are listed in IsWindowChord (TerminalTabs.cs), so they still work while a shell has focus.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private const int MenuSlideMs = 140;

        /// <summary>
        /// True for F10, however it arrived.
        /// </summary>
        /// <remarks>
        /// Win32 sends WM_SYSKEYDOWN for F10 whether or not Alt is held - it is the key that
        /// opens a window's menu - so WPF reports it as Key.System with the real key parked in
        /// SystemKey, exactly like an Alt chord. Testing e.Key against Key.F10 therefore never
        /// matches, which is why the Shift+F10 shell-menu binding was silently dead until this
        /// went in. Any future F10 binding has to go through here.
        /// </remarks>
        private static bool IsF10(System.Windows.Input.KeyEventArgs e) =>
            e.Key == System.Windows.Input.Key.F10
            || (e.Key == System.Windows.Input.Key.System
                && e.SystemKey == System.Windows.Input.Key.F10);

        // One key per pane. The right pane is not the left one's mirror - it is opened and closed
        // independently and often holds a shell while the left holds a listing - so a single
        // shared setting would restore the wrong thing for one of them every time.
        private static string MenuBarKey(FilePane pane) =>
            "MenuBarHidden" + (pane.Name == "RightPane" ? "R" : "L");

        /// <summary>Restore both panes. No animation on the way in - it would play at launch.</summary>
        private void InitMenuBar()
        {
            foreach (var p in new[] { LeftPane, RightPane })
            {
                if (Services.ThemeManager.GetSetting(MenuBarKey(p)) != "1") continue;
                p.MenuBarHidden = true;
                ApplyMenuBar(p, animate: false);
            }
        }

        /// <summary>Toggle the FOCUSED pane's menubar. Bound to Ctrl+F10.</summary>
        internal void ToggleMenuBar() => SetMenuBar(Pane, !Pane.MenuBarHidden, animate: true);

        /// <summary>The bars' right-click "Hide menu bar" row (FilePane.xaml BarMenu). Acts on
        /// the pane whose bar was clicked, which is not necessarily the focused one.</summary>
        internal void HideMenuBarFor(FilePane pane) => SetMenuBar(pane, hidden: true, animate: true);

        /// <summary>
        /// Set one pane's menubar state. Reachable from the window so the elevated startup shell
        /// can come up bare (Elevation.cs) without faking a key press.
        /// </summary>
        internal void SetMenuBar(FilePane pane, bool hidden, bool animate, bool persist = true)
        {
            if (pane.MenuBarHidden == hidden) return;
            pane.MenuBarHidden = hidden;

            // persist:false for the admin window's bare startup layout (Elevation.cs). That is
            // how this window opens, not a preference the user expressed - saving it would hide
            // the menubar in their ordinary window the next time they started it too.
            if (persist) Services.ThemeManager.SetSetting(MenuBarKey(pane), hidden ? "1" : "0");

            ApplyMenuBar(pane, animate);

            // A collapsed row cannot hold keyboard focus, and leaving it there would strand the
            // caret on an invisible address box. Hand it to whatever the tab is actually showing.
            if (hidden && ReferenceEquals(pane, Pane)) FocusPaneContent();
        }

        /// <summary>
        /// One flag, every bar: the pane's hidden state reaches whichever bar its tabs wear -
        /// the folder location row, the shell bar, the editor bar. The rows for tab kinds that
        /// are not currently showing sit inside collapsed hosts, so setting them too is free and
        /// means a later tab switch comes up already matching the flag.
        /// </summary>
        private static void ApplyMenuBar(FilePane pane, bool animate)
        {
            bool hidden = pane.MenuBarHidden;
            SetLocationRow(pane, hidden, animate);   // keeps its own listing-only guard
            // 32 is these bars' fixed height in FilePane.xaml (matched to the location row's
            // natural height) - the slide has to hand back exactly that, not NaN.
            if (pane.TerminalBarRow != null) SetBarRow(pane.TerminalBarRow, hidden, animate, 32);
            if (pane.EditorBarRow   != null) SetBarRow(pane.EditorBarRow,   hidden, animate, 32);
        }

        /// <summary>
        /// Slide the row shut or open. Height is animated rather than Visibility toggled: a
        /// straight collapse pops, and the row is the thing you are looking at when you press
        /// the key, so the pop is exactly where the eye already is.
        /// </summary>
        private static void SetLocationRow(FilePane pane, bool hidden, bool animate)
        {
            var row = pane.LocationRow;
            if (row == null) return;

            // The row only ever belongs to a LISTING tab (PaneBars.cs WearsLocationRow). Every
            // other kind of tab wears its own bar, and handing this one back on top of that bar
            // is what put TWO identical stacked bars on a shell or document tab: Ctrl+F10 calls
            // straight through SetMenuBar without going near a tab switch, so it never consulted
            // the tab kind at all. Forced hidden here rather than in each caller, so the rule
            // holds for every path that reaches the row.
            if (!WearsLocationRow(pane.Active)) hidden = true;

            SetBarRow(row, hidden, animate, double.NaN);
        }

        /// <summary>
        /// Slide any bar row shut or open. <paramref name="openHeight"/> is what the row gets
        /// back once fully open: NaN (auto) for the location row, whose height the toolbar mode
        /// and app scale decide, or the fixed height a bar was authored at (the shell and editor
        /// bars' 32) - handing those NaN would let them shrink to their content and misalign the
        /// pane's content start across tab kinds.
        /// </summary>
        private static void SetBarRow(FrameworkElement row, bool hidden, bool animate, double openHeight)
        {
            if (!animate)
            {
                row.BeginAnimation(FrameworkElement.HeightProperty, null);
                row.Height = hidden ? 0 : openHeight;
                row.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
                return;
            }

            // A fixed-height bar animates to its authored height; the auto-sized location row is
            // measured, not assumed - its height depends on the toolbar's display mode and on
            // the app scale, so a hard-coded number would animate to the wrong place the moment
            // either changed.
            double open = !double.IsNaN(openHeight) ? openHeight
                        : row.ActualHeight > 0 ? row.ActualHeight : Measured(row);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var dur  = new Duration(TimeSpan.FromMilliseconds(MenuSlideMs));

            if (hidden)
            {
                var shut = new DoubleAnimation(open, 0, dur) { EasingFunction = ease };

                // Collapse only AFTER the slide finishes. Setting it up front would take the row
                // out of layout immediately and there would be nothing left to animate.
                shut.Completed += (_, _) =>
                {
                    if (row.Height <= 0.5) row.Visibility = Visibility.Collapsed;
                };
                row.BeginAnimation(FrameworkElement.HeightProperty, shut);
                return;
            }

            row.Visibility = Visibility.Visible;
            row.Height = 0;

            var openAnim = new DoubleAnimation(0, open, dur) { EasingFunction = ease };

            // Hand the height back at the end - auto (NaN) for the location row so a later
            // toolbar reflow or scale change can still grow it, the authored height for a fixed
            // bar. Leaving the animation holding it would freeze the row either way.
            openAnim.Completed += (_, _) =>
            {
                row.BeginAnimation(FrameworkElement.HeightProperty, null);
                row.Height = openHeight;
            };
            row.BeginAnimation(FrameworkElement.HeightProperty, openAnim);
        }

        /// <summary>Natural height of a row that has never been laid out (hidden at startup).</summary>
        private static double Measured(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize.Height;
        }

        /// <summary>Put focus on the tab's content, so it is never left on a hidden row.</summary>
        private void FocusPaneContent()
        {
            var t = Pane.Active;
            if (t != null && t.IsTerminal && t.Term != null) t.Term.Focus();
            else if (t != null && t.Editor != null) t.Editor.TextArea.Focus();
            else Pane.ResultsList.Focus();
        }
    }
}
