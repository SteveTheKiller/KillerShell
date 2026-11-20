using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerFamily.Accessibility;

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
        private MenuItem? _cardHead;
        // The language the card was built in: its captions are plain text, so a language switch rebuilds it.
        private string? _menuLanguage;
        internal TerminalShellKind ShellKind { get; set; } = TerminalShellKind.PowerShell;
        internal event Action<MenuItem>? LaunchSubmenuOpening;

        /// <summary>
        /// What the card's header shows: an icon, the shell's name and where it is. Supplied by the
        /// window, which knows the profile and the tab; with none, the card opens without a header.
        /// </summary>
        internal Func<(FrameworkElement? Icon, string Title, string Meta)>? CardHeaderSource { get; set; }

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

        // Every other request for the menu (Shift+F10 the shell did not take, a screen reader's
        // context action) takes the same path as Apps, so the header is current and the card
        // opens at the caret instead of WPF opening the cached card as it was last left.
        protected override void OnContextMenuOpening(ContextMenuEventArgs e)
        {
            e.Handled = true;
            ShowMenu(keyboard: true);
        }

        /// <summary>
        /// Open the card at the pointer, building it once.
        /// </summary>
        /// <remarks>
        /// Right-click OPENS A MENU rather than pasting, which is the other terminal convention.
        /// A menu is the safer default: a stray right-click that pastes a clipboard full of
        /// commands into a live admin shell is a genuinely bad afternoon. Middle-click still
        /// pastes for anyone who wants the fast path (OnMouseDown).
        /// </remarks>
        private void ShowMenu(bool keyboard = false)
        {
            if (_menu == null || _menuLanguage != KillerShell.Controls.CardMenus.Loc("Str_Card_Actions"))
            {
                _menu = BuildMenu();
                _menuLanguage = KillerShell.Controls.CardMenus.Loc("Str_Card_Actions");
            }
            ContextMenu = _menu;

            // The header is about where the shell is NOW, so it is filled on every opening.
            if (_cardHead != null)
            {
                var source = CardHeaderSource?.Invoke();
                _cardHead.Visibility = source.HasValue ? Visibility.Visible : Visibility.Collapsed;
                if (source is { } head) KillerShell.Controls.CardMenus.SetHeader(_cardHead, head.Icon, head.Title, head.Meta);
            }

            // Copy is the one button whose availability changes: with no selection there is
            // nothing to copy, and a lit button that does nothing is worse than a dim one.
            KillerShell.Controls.CardMenus.SetTile(_menu, "copy", _hasSelection);
            // cmd.exe has no PowerShell $PROFILE. The same card belongs to both terminal skins,
            // so the tiles stay in place for discovery, dimmed where they do not apply.
            bool prompt = ShellKind is TerminalShellKind.PowerShell or TerminalShellKind.Wsl;
            KillerShell.Controls.CardMenus.SetTile(_menu, "editprompt", prompt);
            KillerShell.Controls.CardMenus.SetTile(_menu, "resetprompt", prompt);
            KillerShell.Controls.CardMenus.SetTile(_menu, "reloadprofile", ShellKind == TerminalShellKind.PowerShell);
            KillerShell.Controls.CardMenus.SetTile(_menu, "profile", ShellKind == TerminalShellKind.PowerShell);

            _menu.PlacementTarget = this;
            _menu.Placement = keyboard
                ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                : System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _menu.PlacementRectangle = keyboard
                ? new Rect(LeftInset + _buf.CursorCol * _cellW,
                    Math.Min((_buf.CursorRow + _scroll) * _cellH, Math.Max(0, ActualHeight - _cellH)),
                    _cellW, _cellH)
                : Rect.Empty;
            _menu.IsOpen = true;
        }

        // Glyphs as codepoints, never literal private-use characters: every file under Terminal
        // is BOM-less UTF-8 with zero non-ASCII bytes, and typing one of these directly is what
        // made KillerPDF's release.ps1 PS7-only.
        private static string Glyph(int cp) => ((char)cp).ToString();

        // Keep the visible screen and cursor intact: only the shell knows which cells
        // belong to a pending multiline prompt. This action never sends shell input.
        internal void ClearScrollback()
        {
            _buf.ClearScrollback();
            _scroll = 0;
            ClearSelection();
            QueueAutomationChange(text: true);
            InvalidateVisual();
        }

        /// <summary>
        /// The terminal card: a header naming the shell and its folder, then the commands as tiles, each with its
        /// shortcut. New shell and Edit profile open their lists beside the card.
        /// </summary>
        private ContextMenu BuildMenu()
        {
            var m = KillerShell.Controls.CardMenus.TileCard();

            _cardHead = KillerShell.Controls.CardMenus.Slot();
            _cardHead.Tag = "cardhead";
            m.Items.Add(_cardHead);
            KillerShell.Controls.CardMenus.WatchKeyboardStart(m);

            m.Items.Add(KillerShell.Controls.CardMenus.Tiles(m,
            [
                KillerShell.Controls.CardMenus.Tile("copy", 0xE8C8, "Str_Term_Copy", "Str_Card_Copy", "Ctrl+Shift+C", () =>
                {
                    if (CopySelection()) ClearSelection();
                }),
                KillerShell.Controls.CardMenus.Tile("paste", 0xE77F, "Str_Term_Paste", "Str_Card_Paste", "Ctrl+Shift+V", Paste),
                KillerShell.Controls.CardMenus.Tile("selectall", 0xE8B3, "Str_Term_SelectAll", "Str_Card_SelectAll", "Ctrl+Shift+A", SelectAll),
                // The eraser, not MDL2's Clear X: an X on a card reads as Close.
                KillerShell.Controls.CardMenus.Tile("clear", 0xE75C, "Str_Term_Clear", "Str_Card_Clear", "Ctrl+Shift+L", ClearScrollback),
                // The reverse of "open a terminal here": whatever the shell has cd'd to, opened as a
                // folder tab. The buffer tracks the working directory already (OSC 7).
                KillerShell.Controls.CardMenus.Tile("folder", 0xE8B7, "Str_Term_OpenFolder", "Str_Card_Folder", "Ctrl+Shift+T",
                    () => MenuCommand?.Invoke(TerminalMenuCommand.OpenFolder)),
                // No Close here: one click on it ended a running shell. Ctrl+W and the tab's own
                // close button close the terminal.
                KillerShell.Controls.CardMenus.LiveFlyout(m, "newshell", Glyph(0xE756), KillerShell.Controls.CardMenus.Loc("Str_Term_NewShell"), "F8",
                    holder => LaunchSubmenuOpening?.Invoke(holder)),
            ],
            [
                KillerShell.Controls.CardMenus.Tile("fonts", 0xE8D2, "Str_Term_Fonts", null, "Ctrl+Shift+,", () => RunMenuCommand(Key.OemComma)),
                // The prompt is a SCRIPT the user owns, not a setting, so the card opens the file.
                // Reset is next to it because a script you are encouraged to edit needs a way back.
                KillerShell.Controls.CardMenus.Tile("editprompt", 0xE70F, "Str_Term_EditPrompt", null, "Ctrl+Shift+E", () => RunMenuCommand(Key.E)),
                KillerShell.Controls.CardMenus.Tile("resetprompt", 0xE777, "Str_Term_ResetPrompt", null, "Ctrl+Shift+R", () => RunMenuCommand(Key.R)),
                // Run inside THIS shell rather than starting a helper process, so each PowerShell
                // resolves its own $PROFILE and the output stays where the user asked.
                KillerShell.Controls.CardMenus.Tile("reloadprofile", 0xE895, "Str_Prof_Reload", null, "Ctrl+Shift+Q", () => RunMenuCommand(Key.Q)),
                // The user's $PROFILE, a different file from the prompt. A list, because the two
                // PowerShell hosts do not share one (ProfileMenu.cs fills it).
                KillerShell.Controls.CardMenus.LiveFlyout(m, "profile", Glyph(0xE70F), KillerShell.Controls.CardMenus.Loc("Str_Prof_Edit"), "Ctrl+,",
                    holder => ProfileSubmenuOpening?.Invoke(holder)),
            ]));

            return m;
        }

        internal bool HandleMenuShortcut(KeyEventArgs e)
        {
            if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return false;
            // Clear is a tile on the card, so its chord runs it directly.
            if (e.Key == Key.L) { ClearScrollback(); return true; }
            return e.Key is Key.OemComma or Key.E or Key.R or Key.Q && RunMenuCommand(e.Key);
        }

        /// <summary>
        /// The card's chorded commands, shared by the tiles and the keyboard, so the two never drift apart.
        /// A command the shell cannot carry out does nothing but still counts as handled.
        /// </summary>
        private bool RunMenuCommand(Key key)
        {
            bool prompt = ShellKind is TerminalShellKind.PowerShell or TerminalShellKind.Wsl;
            switch (key)
            {
                case Key.OemComma: MenuCommand?.Invoke(TerminalMenuCommand.Fonts); return true;
                case Key.E: if (prompt) MenuCommand?.Invoke(TerminalMenuCommand.EditPrompt); return true;
                case Key.R: if (prompt) MenuCommand?.Invoke(TerminalMenuCommand.ResetPrompt); return true;
                case Key.Q:
                    // Doubled apostrophes: the text lands inside a single-quoted PowerShell string, and
                    // several locales spell this message with one.
                    if (ShellKind == TerminalShellKind.PowerShell)
                        Send("try { . $PROFILE; Write-Host '"
                             + Shell.MainWindow.LocStatic("Str_Term_ProfileReloaded").Replace("'", "''")
                             + "' -ForegroundColor Green } catch { Write-Error $_ }\r");
                    return true;
                default: return false;
            }
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
