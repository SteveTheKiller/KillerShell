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
            if (_copyItem != null) _copyItem.IsEnabled = _hasSelection;

            _menu.PlacementTarget = this;
            _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _menu.IsOpen = true;
        }

        // Glyphs as codepoints, never literal private-use characters: every file under Terminal
        // is BOM-less UTF-8 with zero non-ASCII bytes, and typing one of these directly is what
        // made KillerPDF's release.ps1 PS7-only.
        private static string Glyph(int cp) => ((char)cp).ToString();

        private ContextMenu BuildMenu()
        {
            var m = new ContextMenu();

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
            Row(m, "Str_Term_Clear", Glyph(0xE894), null, () => Send("cls\r"));

            m.Items.Add(new Separator());

            // These belong to the WINDOW - it owns the panes and the tab strip - so they are
            // raised rather than carried out here. The control does not know it is in a tab.
            Row(m, "Str_Term_NewShell", Glyph(0xE756), "F8",
                () => MenuCommand?.Invoke(TerminalMenuCommand.NewShell));

            // The reverse of "open a terminal here": whatever the shell has cd'd to, opened as a
            // folder tab. The buffer tracks the working directory already (OSC 7), so this is
            // free - and after a few minutes of cd-ing around, getting the folder listing to
            // follow is otherwise a copy of the path and a paste into the address bar.
            Row(m, "Str_Term_OpenFolder", Glyph(0xE8B7), null,
                () => MenuCommand?.Invoke(TerminalMenuCommand.OpenFolder));

            Row(m, "Str_Term_Fonts", Glyph(0xE8D2), null,
                () => MenuCommand?.Invoke(TerminalMenuCommand.Fonts));

            // The prompt is a SCRIPT the user owns, not a setting, so the menu opens the file
            // rather than a dialog of checkboxes over it - anything a dialog could offer, the
            // file already does, and better. Reset is next to it because a script you are
            // encouraged to edit needs a way back.
            Row(m, "Str_Term_EditPrompt", Glyph(0xE70F), null,
                () => MenuCommand?.Invoke(TerminalMenuCommand.EditPrompt));

            Row(m, "Str_Term_ResetPrompt", Glyph(0xE777), null,
                () => MenuCommand?.Invoke(TerminalMenuCommand.ResetPrompt));

            // The user's $PROFILE, which is a DIFFERENT file from the prompt above it and a far
            // more common thing to want: the prompt script is ours and only runs in here, while
            // the profile is theirs and runs in every shell they open anywhere.
            //
            // A submenu because the two PowerShell hosts do not share one, and picking the wrong
            // one is most of the "why is my profile not loading" in the world. Its rows arrive
            // as it opens; the placeholder child is only what makes WPF draw the arrow and fire
            // SubmenuOpened at all, and it is replaced before it can be seen.
            var profile = new MenuItem { InputGestureText = "Ctrl+," };
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

        /// <summary>
        /// One row. The header is a resource reference so a language switch repaints the menu in
        /// place, the same as every other menu in the app.
        /// </summary>
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
