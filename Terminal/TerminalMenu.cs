using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// The terminal's right-click menu. Part of TerminalControl.
//
// Built in code rather than in XAML because TerminalControl is a raw drawing surface with no
// markup of its own - it renders GlyphRuns straight onto a DrawingContext, which is the whole
// reason it is fast enough to stream a build log.
//
// Rows are the ones a terminal is expected to have and nothing more. Every row is a gesture
// that already exists on the keyboard, so the menu is a way to DISCOVER them rather than a
// second set of commands that can drift out of step with the first.
namespace KillerShell.Terminal
{
    internal sealed partial class TerminalControl
    {
        /// <summary>Raised for rows the WINDOW owns: it has the panes and the tabs, not us.</summary>
        public event Action<TerminalMenuCommand>? MenuCommand;

        private ContextMenu? _menu;
        private MenuItem? _copyItem;
        private MenuItem? _reloadProfileItem;
        internal TerminalShellKind ShellKind { get; set; } = TerminalShellKind.PowerShell;
        internal event Action<MenuItem>? LaunchSubmenuOpening;

        /// <summary>
        /// Raised as the Edit profile submenu opens, carrying the row for the window to fill.
        /// </summary>
        /// <remarks>
        /// Filled by the window rather than here on purpose: knowing which PowerShell hosts are
        /// on this machine means probing the filesystem and starting processes, and this class
        /// draws glyphs onto a DrawingContext. It hands over the row and lets the window decide
        /// what goes in it (ProfileMenu.cs).
        /// </remarks>
        internal event Action<MenuItem>? ProfileSubmenuOpening;

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            Focus();
            ShowMenu();
            e.Handled = true;
            base.OnMouseRightButtonUp(e);
        }

        /// <summary>
        /// Open the menu at the pointer, building it once.
        /// </summary>
        /// <remarks>
        /// Right-click OPENS A MENU rather than pasting, which is the other terminal convention.
        /// A menu is the safer default: a stray right-click that pastes a clipboard full of
        /// commands into a live admin shell is a genuinely bad afternoon. Middle-click still
        /// pastes for anyone who wants the fast path (OnMouseDown).
        /// </remarks>
        private void ShowMenu()
        {
            _menu ??= BuildMenu();

            // Copy is the one row whose availability changes: with no selection there is nothing
            // to copy, and a lit row that does nothing is worse than a dim one.
            _copyItem?.IsEnabled = _hasSelection;
            // cmd.exe has no PowerShell $PROFILE. The same menu belongs to both terminal skins,
            // so keep the row visible for discovery but make its scope honest.
            _reloadProfileItem?.IsEnabled = ShellKind == TerminalShellKind.PowerShell;

            _menu.PlacementTarget = this;
            _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _menu.IsOpen = true;
        }

        // Glyphs as codepoints, never literal private-use characters: every file under Terminal
        // is BOM-less UTF-8 with zero non-ASCII bytes, and typing one of these directly is what
        // made KillerPDF's release.ps1 PS7-only.
        private static string Glyph(int cp) => ((char)cp).ToString();

        private OpaqueContextMenu BuildMenu()
        {
            var m = new OpaqueContextMenu();

            _copyItem = Row(m, "Str_Term_Copy", Glyph(0xE8C8), "Ctrl+Shift+C", () =>
            {
                if (CopySelection()) ClearSelection();
            });

            Row(m, "Str_Term_Paste",     Glyph(0xE77F), "Ctrl+Shift+V", Paste);
            Row(m, "Str_Term_SelectAll", Glyph(0xE8B3), "Ctrl+Shift+A", SelectAll);

            m.Items.Add(new Separator());

            // Clear is the SHELL's job, not ours. Wiping our own buffer would leave the shell
            // believing it had already drawn a prompt, so the next keystroke would paint over
            // nothing. Sending the command brings the prompt back the way the shell wants it,
            // and cls works the same in pwsh, powershell and cmd.
            Row(m, "Str_Term_Clear", Glyph(0xE894), "Ctrl+Shift+L", () => Send(ShellKind == TerminalShellKind.Wsl || ShellKind == TerminalShellKind.Custom ? "clear\r" : "cls\r"));

            m.Items.Add(new Separator());

            // These belong to the WINDOW - it owns the panes and the tab strip - so they are
            // raised rather than carried out here. The control does not know it is in a tab.
            var launches = new MenuItem { InputGestureText = "F8" };
            launches.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Term_NewShell");
            var launchIcon = new TextBlock { Text = Glyph(0xE756) };
            launchIcon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            launches.Icon = launchIcon;
            m.Items.Add(launches);
            launches.Items.Add(new MenuItem());
            launches.SubmenuOpened += (_, _) => LaunchSubmenuOpening?.Invoke(launches);

            // The reverse of "open a terminal here": whatever the shell has cd'd to, opened as a
            // folder tab. The buffer tracks the working directory already (OSC 7), so this is
            // free - and after a few minutes of cd-ing around, getting the folder listing to
            // follow is otherwise a copy of the path and a paste into the address bar.
            Row(m, "Str_Term_OpenFolder", Glyph(0xE8B7), "Ctrl+Shift+T",
                () => MenuCommand?.Invoke(TerminalMenuCommand.OpenFolder));

            Row(m, "Str_Term_Fonts", Glyph(0xE8D2), "Ctrl+Shift+,",
                () => MenuCommand?.Invoke(TerminalMenuCommand.Fonts));

            // The prompt is a SCRIPT the user owns, not a setting, so the menu opens the file
            // rather than a dialog of checkboxes over it - anything a dialog could offer, the
            // file already does, and better. Reset is next to it because a script you are
            // encouraged to edit needs a way back.
            var editPrompt = Row(m, "Str_Term_EditPrompt", Glyph(0xE70F), "Ctrl+Shift+E",
                () => MenuCommand?.Invoke(TerminalMenuCommand.EditPrompt));
            editPrompt.IsEnabled = ShellKind is TerminalShellKind.PowerShell or TerminalShellKind.Wsl;

            var resetPrompt = Row(m, "Str_Term_ResetPrompt", Glyph(0xE777), "Ctrl+Shift+R",
                () => MenuCommand?.Invoke(TerminalMenuCommand.ResetPrompt));
            resetPrompt.IsEnabled = ShellKind is TerminalShellKind.PowerShell or TerminalShellKind.Wsl;

            // Run inside THIS shell rather than starting a helper process. PowerShell 7 and
            // Windows PowerShell then each resolve their own $PROFILE, and any output, success,
            // or error stays in the terminal where the user asked for the reload.
            _reloadProfileItem = Row(m, "Str_Prof_Reload", Glyph(0xE895), "Ctrl+Shift+Q", () =>
                // Doubled apostrophes: the text lands inside a single-quoted PowerShell string, and
                // several locales spell this message with one.
                Send("try { . $PROFILE; Write-Host '"
                     + Shell.MainWindow.LocStatic("Str_Term_ProfileReloaded").Replace("'", "''")
                     + "' -ForegroundColor Green } catch { Write-Error $_ }\r"));
            _reloadProfileItem.IsEnabled = ShellKind == TerminalShellKind.PowerShell;

            // The user's $PROFILE, which is a DIFFERENT file from the prompt above it and a far
            // more common thing to want: the prompt script is ours and only runs in here, while
            // the profile is theirs and runs in every shell they open anywhere.
            //
            // A submenu because the two PowerShell hosts do not share one, and picking the wrong
            // one is most of the "why is my profile not loading" in the world. Its rows arrive
            // as it opens; the placeholder child is only what makes WPF draw the arrow and fire
            // SubmenuOpened at all, and it is replaced before it can be seen.
            var profile = new MenuItem { InputGestureText = "Ctrl+,", IsEnabled = ShellKind == TerminalShellKind.PowerShell };
            profile.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Prof_Edit");

            var profileIcon = new TextBlock { Text = Glyph(0xE70F) };
            profileIcon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            profile.Icon = profileIcon;

            profile.Items.Add(new MenuItem());
            profile.SubmenuOpened += (_, _) => ProfileSubmenuOpening?.Invoke(profile);
            m.Items.Add(profile);

            m.Items.Add(new Separator());
            Row(m, "Str_Term_Close", Glyph(0xE8BB), "Ctrl+W",
                () => MenuCommand?.Invoke(TerminalMenuCommand.CloseTab));

            return m;
        }

        internal bool HandleMenuShortcut(KeyEventArgs e)
        {
            if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return false;
            string? gesture = e.Key switch
            {
                Key.L => "Ctrl+Shift+L",
                Key.OemComma => "Ctrl+Shift+,",
                Key.E => "Ctrl+Shift+E",
                Key.R => "Ctrl+Shift+R",
                Key.Q => "Ctrl+Shift+Q",
                _ => null,
            };
            if (gesture == null) return false;
            _menu ??= BuildMenu();
            foreach (var entry in _menu.Items)
                if (entry is MenuItem item && item.InputGestureText == gesture)
                {
                    if (item.IsEnabled && (e.Key != Key.Q || ShellKind == TerminalShellKind.PowerShell))
                        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    return true;
                }
            return false;
        }

        /// <summary>Build a menu row whose label follows the active language.</summary>
        private static MenuItem Row(ContextMenu m, string key, string glyph, string? gesture, Action go)
        {
            var item = new MenuItem { InputGestureText = gesture ?? string.Empty };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);

            var icon = new TextBlock { Text = glyph };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            item.Icon = icon;

            item.Click += (_, _) => go();
            m.Items.Add(item);
            return item;
        }
    }

    /// <summary>Menu rows the terminal cannot carry out on its own.</summary>
    internal enum TerminalMenuCommand
    {
        NewShell,
        OpenFolder,
        Fonts,
        EditPrompt,
        ResetPrompt,
        CloseTab,
    }
}
