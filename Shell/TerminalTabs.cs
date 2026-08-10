using System;
using System.Windows;
using KillerShell.Models;
using KillerShell.Terminal;

// Shell tabs, and where they land. Partial of MainWindow.
//
// The placement rule: a shell opens as a tab in the pane that HAS FOCUS, like every other tab
// the app makes. Nothing is moved, nothing is split, and the tab appears where you were already
// looking.
//
// This replaced a fixed "always the second pane, open it if it is shut" rule. That one read
// well - folders left, shells right, a layout you stop thinking about - but it meant a keypress
// could tear a window into two panes you had deliberately closed, and it always moved your
// focus to the other side of the window to do it. A shortcut whose effect depends on which pane
// it decides to commandeer is a shortcut you have to watch. Predictable beats tidy: if you want
// the shell beside a folder, F8 and put it there.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Open a shell for <paramref name="folder"/> as a tab in the focused pane.
        /// </summary>
        internal void OpenShell(TerminalProfile profile, string? folder = null)
        {
            folder = Resolve(folder);

            // Before anything is spawned: the child inherits our environment block, so
            // PSModulePath has to carry the bundled modules by the time CreateProcess runs
            // (Modules.cs). Cheap after the first call.
            EnsureBundledModules();

            if (profile.Elevated)
            {
                // An elevated process cannot attach to an unelevated pseudoconsole - it is a
                // UAC integrity boundary, not an API gap. So we relaunch OURSELVES elevated and
                // let that instance host the shell, which is also how Windows Terminal handles
                // it. Elevation.cs owns the relaunch.
                RelaunchElevated(profile, folder);
                return;
            }

            // Non-elevated profile in an elevated window: can't attach a non-elevated shell to an
            // elevated pseudoconsole (same UAC boundary). Launch a new non-elevated window instead.
            // This is the mirror of RelaunchElevated above: F8 pressed in an admin-only KillerShell
            // should open a new non-elevated window, not try (and fail) to add a tab here.
            if (IsElevated)
            {
                OpenUnelevated(folder);
                return;
            }

            // The pane you are IN. A shell used to be forced into the right-hand pane, opening
            // it if it was shut, on the theory that folders left / shells right is a habit you
            // can build. In practice it meant a key you pressed while working in one pane threw
            // a tab into the other one and moved your focus there, and it could split a window
            // open that you had deliberately closed. Predictable beats tidy: every shortcut that
            // makes a tab now makes it where you are standing.
            var target = Pane;        // Panes.cs - the focused pane
            var tab = CreateTerminalTabIn(target, profile, folder);
            if (tab == null) return;

            FocusPane(target);        // Panes.cs - the shell is what you just asked for
            ActivateTab(tab);
        }

        /// <summary>
        /// The shell an elevated relaunch was started FOR (Elevation.cs): one pane, no sidebar,
        /// no menubar, wide enough for the Killer scripts.
        /// </summary>
        /// <remarks>
        /// It opens in the pane that is already there, which is now simply what OpenShell does
        /// for everything. This used to need an explicit inPlace flag to opt out of the forced
        /// second pane; that rule is gone, so the flag went with it.
        /// </remarks>
        internal void OpenStartupShell(TerminalProfile profile, string? folder)
        {
            OpenShell(profile, folder);

            // Only strip the window down if a terminal actually appeared. Every line below is
            // "this window exists to run ONE shell" - close the other tabs, hide the menubar,
            // shut the sidebar, widen past 90 columns - and all of it is wrong if OpenShell
            // bailed out instead of making one. It did exactly that when the profile arrived
            // unelevated in an elevated window: the request left through OpenUnelevated and this
            // ran anyway, leaving a chrome-less FILE BROWSER with no menubar, no tree and no
            // address bar - no way to tell what folder it is in (2026-08-08). The
            // profile bug is fixed in Elevation.cs ApplyStartupShell; this guard means any future
            // bail-out degrades to an ordinary usable window instead of a blind one.
            if (Pane.Active?.Term == null) return;

            // Just the shell. The pane seeds itself with a folder tab at startup so the strip is
            // never empty (Tabs.cs), which is right for an ordinary window and wrong for this
            // one: it was launched to run ONE shell, and the leftover home tab is a second thing
            // to look at that nobody asked for. Closed after the shell exists, so the pane is
            // never momentarily tabless and cannot seed a replacement.
            // Copied to an array first: FinishCloseTab mutates the collection being walked.
            var keep = Pane.Active;
            var others = new SearchTab[Pane.Tabs.Count];
            Pane.Tabs.CopyTo(others, 0);
            foreach (var t in others)
                if (!ReferenceEquals(t, keep)) FinishCloseTab(t);   // Tabs.cs

            SetMenuBar(Pane, hidden: true, animate: false, persist: false);   // MenuBar.cs

            // Sidebar shut, without persisting it: this is how the ADMIN window opens, not a
            // preference the user expressed, and writing the setting would silently close the
            // tree in their ordinary window too the next time they started it.
            if (_treeOpen)                                    // TreePanel.cs
            {
                _treeOpen = false;
                ApplyTreePanel(animate: false);
            }

            WidenForShell();
        }

        /// <summary>
        /// Give the shell at least <see cref="ShellCols"/> columns, growing the window if it is
        /// too narrow. Runs after layout, because the answer depends on the terminal's real cell
        /// width and on how much of the window the chrome is actually taking.
        /// </summary>
        /// <remarks>
        /// Every Killer script draws to a fixed width - 85 columns for most of them, 90 for
        /// AMORT - so a window narrower than that wraps every rule and every banner and the
        /// output is unreadable. The number is measured from the terminal rather than assumed in
        /// pixels: the cell width moves with the terminal font and its size, both of which are
        /// user settings now (Fonts.cs).
        /// </remarks>
        private const int ShellCols = 92;   // 90 for AMORT, plus two so a full-width rule cannot wrap

        private void WidenForShell()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var term = Pane.Active?.Term;
                if (term == null) return;

                double want = term.WidthForColumns(ShellCols);
                if (want <= 0) return;                       // typeface not resolved yet

                // What the chrome costs right now, measured rather than guessed: sidebar, pane
                // borders, gutter and window edges all move with settings and with the theme.
                double chrome = ActualWidth - term.ActualWidth;
                if (term.ActualWidth <= 0 || chrome <= 0) return;

                double target = Math.Ceiling(want + chrome);
                if (target <= ActualWidth) return;            // already wide enough

                // Never wider than the screen it is on, and never off the left edge.
                double max = SystemParameters.WorkArea.Width;
                Width = Math.Min(target, max);
                Left  = Math.Max(SystemParameters.WorkArea.Left,
                                 Left - (Width - ActualWidth) / 2);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private string Resolve(string? folder)
        {
            if (!string.IsNullOrWhiteSpace(folder) && System.IO.Directory.Exists(folder))
                return folder!;

            var t = Pane.Active;
            if (t != null && t.IsBrowsing && !IsThisPc(t.CurrentFolder)      // Browse.cs
                && System.IO.Directory.Exists(t.CurrentFolder))
                return t.CurrentFolder;

            return HomeFolder;        // AddressBar.cs
        }

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        private SearchTab? CreateTerminalTabIn(FilePane pane, TerminalProfile profile, string folder)
        {
            var keep = Pane;
            FocusPaneQuiet(pane);                 // Panes.cs

            SearchTab tab;
            try
            {
                tab = CreateTab();                // Tabs.cs - registers it in this pane
            }
            finally { FocusPaneQuiet(keep); }

            var term = new TerminalControl(profile.Skin);
            tab.Term = term;
            tab.TabGlyph = profile.Glyph;
            tab.TermExePath = profile.ExePath;
            // The tab title is WHERE the shell is, not what it happens to be running - Browse.cs's
            // own FolderTitle so a shell tab reads the same way a folder tab does. This used to
            // follow the shell's own OSC 0/2 title instead (a long build showing up in the tab
            // rather than staying "PowerShell"), but the title should be the location, and one
            // tab title should not mean two different things depending on which kind of tab it
            // is (2026-08-02).
            tab.Title = FolderTitle(folder);           // Browse.cs
            tab.IsBrowsing = false;
            tab.CurrentFolder = folder;
            // So the address row reads the shell's folder instead of "no folder selected" - the
            // path is still meaningful on a shell tab, which is why the nav buttons stay.
            tab.RootPath = folder;

            // Menu rows the control cannot do itself, because they are about the pane and the
            // tab strip rather than about the shell (TerminalMenu.cs).
            term.MenuCommand += cmd => Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (cmd)
                {
                    case TerminalMenuCommand.NewShell:
                        OpenShell(TerminalProfile.PowerShell(), tab.CurrentFolder);
                        break;
                    case TerminalMenuCommand.OpenFolder:
                        OpenFolderTabLeft(tab.CurrentFolder);
                        break;
                    case TerminalMenuCommand.Fonts:
                        FontsRow_Click(this, new RoutedEventArgs());   // Fonts.cs
                        break;
                    case TerminalMenuCommand.EditPrompt:
                        EditPromptScript();                            // Terminal/PromptScript.cs
                        break;
                    case TerminalMenuCommand.ResetPrompt:
                        ResetPromptWithConfirm();                      // Terminal/PromptScript.cs
                        break;
                    case TerminalMenuCommand.CloseTab:
                        CloseTab(tab);                                 // Tabs.cs
                        break;
                }
            }));

            // The Edit profile submenu is filled by the window as it opens, from the PowerShell
            // hosts actually on this machine (ProfileMenu.cs). Subscribed here rather than at
            // menu-build time because the menu is not built until the first right-click.
            term.ProfileSubmenuOpening += BuildProfileMenu;

            // And the folder follows a cd, so the OTHER pane can be pointed at it later - and so
            // the tab title does too, now that the title IS the location (see the remark above).
            term.Buffer.DirectoryChanged += dir => Dispatcher.BeginInvoke(new Action(() =>
            {
                tab.CurrentFolder = dir;
                tab.RootPath = dir;
                tab.Title = FolderTitle(dir);   // Browse.cs
                SyncTerminalBar(tab);   // TerminalBar.cs - the cwd readout is the shell's own now
            }));

            term.Exited += _ => Dispatcher.BeginInvoke(new Action(() =>
            {
                // The tab stays open showing the exit line. Closing it out from under the user
                // would throw away whatever the command printed, which is usually the reason
                // they ran it.
                tab.TabGlyph = ((char)0xE711).ToString();   // a cross, so a dead shell reads as dead
            }));

            term.Start(profile.CommandLine, folder);
            return tab;
        }

        // ═══════════════════════════════════════════════════════════
        //  DEMO
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// A shell tab showing canned output, with no process behind it (--demo, DemoMode.cs).
        /// </summary>
        /// <remarks>
        /// Built here rather than through OpenShell on purpose. OpenShell publishes the bundled
        /// modules and, for an elevated profile, relaunches the whole application behind a UAC
        /// prompt; neither belongs in the middle of a screenshot session. Resolve is skipped for
        /// the same reason: it falls back to the user's real home folder for any path that is not
        /// a directory, and every demo path is fabricated, so it would put the actual machine's
        /// home on the shell bar - exactly the leak demo mode exists to prevent.
        ///
        /// The pty-driven wiring CreateTerminalTabIn does - the title from OSC 0/2, the folder
        /// from OSC 7, the exit glyph - is left off because nothing here will ever raise it.
        /// </remarks>
        private SearchTab CreateDemoTerminalTab(string folder, string canned)
        {
            var profile = TerminalProfile.PowerShell();

            var tab = CreateTab();            // Tabs.cs - registers it in the current pane
            var term = new TerminalControl(profile.Skin);
            tab.Term = term;
            tab.TabGlyph = profile.Glyph;
            tab.Title = profile.Name;
            tab.IsBrowsing = false;

            // The working-directory readout is driven purely off these (TerminalBar.cs), so a
            // fabricated folder on the tab is the whole of what the shell bar reads.
            tab.CurrentFolder = folder;
            tab.RootPath = folder;

            term.StartDemo(canned);
            SyncTerminalBar(tab);             // TerminalBar.cs
            return tab;
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIVATION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Swap the pane between its listing and a shell. Called from ActivateTab, so it runs
        /// on every tab switch in either pane.
        /// </summary>
        private void ApplyTerminalView(SearchTab t)
        {
            bool shell = t.IsTerminal;

            Pane.TerminalHost.Visibility = shell ? Visibility.Visible : Visibility.Collapsed;
            Pane.ResultsList.Visibility  = shell ? Visibility.Collapsed : Visibility.Visible;

            // The content is MOVED rather than rebuilt: the control owns a live pty, and a new
            // one per activation would kill the shell on every tab switch.
            Pane.TerminalSlot.Content = shell ? t.Term : null;

            ApplyPaneToolbarMode(shell);
            if (shell) SyncTerminalBar(t);   // TerminalBar.cs - the shell's own strip

            if (shell)
            {
                // Focus has to wait for the swap to lay out, or it lands on an element that is
                // still collapsed and silently does nothing.
                var term = t.Term!;
                Dispatcher.BeginInvoke(new Action(() => term.Focus()),
                                       System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        // Everything on the location row that acts on a LISTING. Nav and the address bar are
        // deliberately absent: a shell has a working directory, so back, forward, up and the
        // path are all still meaningful there. Sorting a shell is not.
        //
        // DetailsHeader is not on the location row - it is the column-heading band directly
        // above the results list - but it belongs here for the same reason: its visibility was
        // decided by the VIEW MODE alone, which knows nothing about what kind of tab is on
        // screen, so switching to a shell or a document while in details view left a row of
        // name / location / size / modified sort buttons sitting on top of a terminal or an
        // empty document. Forced Visible on the way back like the rest of these, then corrected
        // by ApplyResultsView below, which is the one place that owns whether it belongs.
        // DetailsPaneBtn/DetailsPane cover the details/preview strip (Controls/FilePane.xaml,
        // Shell/DetailsPane.cs): it slides out of the bottom of a results pane and reads the
        // pane's OWN selection, which means nothing on a shell, document, Processes, Event
        // Viewer, Performance or Registry Editor tab - none of those have a file selection to
        // describe. Restoring
        // it below just forces Visibility back to Visible like every other entry here; the real
        // open/closed/height state is corrected right after by ApplyDetailsPane (DetailsPane.cs),
        // same as ApplyResultsView/UpdateLocationColumn correct their own entries in this list.
        private static readonly string[] ListingOnlyTools =
        [
            "ViewListBtn", "ViewIconsBtn", "ViewDetailsBtn", "SortBtn", "SortDirButton",
            "ExpandAllButton", "ShowHiddenBtn", "FoldersTopBtn", "PipeBtn", "ExportBtn",
            "DetailsHeader", "DetailsPaneBtn", "DetailsPane",
        ];

        /// <summary>
        /// Hide the listing controls on a shell tab, and hand them back to their own logic on
        /// any other kind. Restoring is done by re-running the normal updaters rather than by
        /// setting Visible here: several of these have their own rules (ExpandAll hides outside
        /// list view, Pipe hides while browsing) and forcing them visible would break those.
        /// </summary>
        private void ApplyPaneToolbarMode(bool shell)
        {
            if (shell)
            {
                foreach (var name in ListingOnlyTools)
                    if (Pane.FindName(name) is FrameworkElement el)
                        el.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var name in ListingOnlyTools)
                if (Pane.FindName(name) is FrameworkElement el)
                    el.Visibility = Visibility.Visible;

            ApplyResultsView();        // ResultsView.cs - view mode owns some of these
            UpdateLocationColumn();    // ViewOptions.cs - browsing owns Pipe
            ApplyDetailsPane(Pane, animate: false);   // DetailsPane.cs - open/closed + height are its own to own
        }

        /// <summary>Tear down a shell when its tab closes. Called from FinishCloseTab.</summary>
        private void CloseTerminal(SearchTab t)
        {
            if (t.Term == null) return;
            if (ReferenceEquals(Pane.TerminalSlot.Content, t.Term)) Pane.TerminalSlot.Content = null;
            t.Term.Close();
            t.Term = null;
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD OWNERSHIP
        // ═══════════════════════════════════════════════════════════
        /// <summary>True while the caret is inside a live shell.</summary>
        internal bool TerminalHasFocus =>
            System.Windows.Input.Keyboard.FocusedElement is TerminalControl;

        /// <summary>
        /// Chords that belong to the WINDOW even while a shell has focus. Everything not
        /// listed here reaches the shell.
        /// </summary>
        /// <remarks>
        /// Deliberately a short list of things that manage tabs, panes and windows rather than
        /// text. Ctrl+C is NOT here: in a terminal it is interrupt, and stealing it would make
        /// a runaway command unkillable. Nor is Ctrl+F, because a shell has its own history
        /// search and the results filter means nothing over a pty.
        /// </remarks>
        private bool IsWindowChord(System.Windows.Input.KeyEventArgs e, bool ctrl, bool shift, bool alt)
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

            // Window and overlay level, no modifier needed.
            if (key == System.Windows.Input.Key.F1) return true;    // shortcuts card
            // F10 arrives as Key.System with the real key in SystemKey (see IsF10 in MenuBar.cs),
            // which the unwrap above has already resolved. Both bare F10 (hide the menubar) and
            // Shift+F10 (the shell context menu) are the window's.
            if (key == System.Windows.Input.Key.F10) return true;

            // F8 opens a shell from ANYWHERE, including from inside one. This used to fall
            // through to the shell, because PSReadLine binds F8 and Shift+F8 to history search
            // and losing that inside a terminal is a real cost. It reads as broken though: F8 is
            // the app's headline key, and a key that means one thing everywhere except inside
            // the feature it opens is worse than a missing history binding. Ctrl+r and prefix
            // plus Up still search history; nothing else opens a shell.
            if (key == System.Windows.Input.Key.F8) return true;
            if (key == System.Windows.Input.Key.F9) return true;    // Processes tab, plain or Ctrl for elevated
            if (key == System.Windows.Input.Key.F11) return true;   // Performance tab
            if (key == System.Windows.Input.Key.F12) return true;   // about card, or Ctrl for Event Viewer

            if (alt) return true;      // Alt chords are the app's: bookmarks, menus, Alt+F4

            if (!ctrl) return false;   // a bare key is always the shell's

            return key switch
            {
                // cycle tabs
                System.Windows.Input.Key.Tab or System.Windows.Input.Key.W or System.Windows.Input.Key.T or System.Windows.Input.Key.OemTilde => true,
                // Ctrl+comma edits $PROFILE (ProfileMenu.cs). The window's, even from inside a
                // shell - editing your profile is the one thing you are most likely to want
                // WHILE looking at a prompt, and PSReadLine does not bind it.
                System.Windows.Input.Key.OemComma => !shift,
                // Ctrl+PageUp/Down moves between tabs, but adding SHIFT makes it the
                // terminal's own scrollback paging (Windows Terminal's binding), so the shift
                // variant has to be let through rather than swallowed here.
                System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown => !shift,
                // Ctrl+1-9 jumps to a tab by number.
                System.Windows.Input.Key.D1 or System.Windows.Input.Key.D2 or System.Windows.Input.Key.D3 or System.Windows.Input.Key.D4 or System.Windows.Input.Key.D5 or System.Windows.Input.Key.D6 or System.Windows.Input.Key.D7 or System.Windows.Input.Key.D8 or System.Windows.Input.Key.D9 => !shift,
                _ => false,
            };
        }

        /// <summary>
        /// Open <paramref name="folder"/> as a folder tab in the LEFT pane.
        /// </summary>
        /// <remarks>
        /// Always the left pane, the mirror of shells always opening in the right one. The two
        /// rules together are the whole layout: folders left, shells right, and either side can
        /// send the other where it is. Sending it to the pane the shell is in would cover the
        /// shell you asked the question from.
        /// </remarks>
        internal void OpenFolderTabLeft(string? folder)
        {
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) return;

            // Focus moves for real, not quietly: the folder is what you just asked to see, so
            // the tree, the search panel and every command should now be pointed at it.
            FocusPane(LeftPane);        // Panes.cs
            ActivateTab(CreateTab());   // Tabs.cs
            _ = NavigateTo(folder!);    // Browse.cs
        }

        // ═══════════════════════════════════════════════════════════
        //  RAIL BUTTON
        // ═══════════════════════════════════════════════════════════
        // Left click opens the one you want nine times out of ten; right click picks. The rail
        // button's own ContextMenu (MainWindow.xaml) carries the four, so WPF opens it on right
        // click with no code here at all.
        private void TerminalRail_Click(object sender, RoutedEventArgs e)
            => OpenShell(TerminalProfile.PowerShell());

        private void RailShellPs_Click(object sender, RoutedEventArgs e)
            => OpenShell(TerminalProfile.PowerShell());

        private void RailShellPsAdmin_Click(object sender, RoutedEventArgs e)
            => OpenShell(TerminalProfile.PowerShell(elevated: true));

        private void RailShellCmd_Click(object sender, RoutedEventArgs e)
            => OpenShell(TerminalProfile.Cmd());

        private void RailShellCmdAdmin_Click(object sender, RoutedEventArgs e)
            => OpenShell(TerminalProfile.Cmd(elevated: true));

        /// <summary>Re-color every open shell after a theme or accent switch.</summary>
        private void RefreshTerminalThemes()
        {
            foreach (var p in new[] { LeftPane, RightPane })
                foreach (var t in p.Tabs)
                    t.Term?.RefreshTheme();
        }
    }
}
