using System;
using System.Windows;
using System.Windows.Controls;
using KillerFamily.Accessibility;

// The document's right-click card. Part of EditorControl.
//
// AvalonEdit ships with NO context menu at all, which is why right-clicking an open file did
// nothing whatsoever - not an empty menu, not a beep, nothing. Every text editor on Windows has
// one, and its absence reads as the app being broken rather than as a missing feature.
//
// Built in code rather than in XAML for the same reason the terminal's is: the control is
// created in code and lives in a slot, so there is no markup of its own for a menu to sit in -
// and a ContextMenu declared as a shared resource would have two editors fighting over one
// instance the moment a second pane opened.
//
// Rows are gestures that already exist on the keyboard, so the menu is a way to DISCOVER them
// rather than a second set of commands that can drift out of step with the first. The four the
// control cannot carry out itself are raised for the window, which owns the tabs and the bar.
namespace KillerShell.Editing
{
    internal sealed partial class EditorControl
    {
        /// <summary>Raised for rows the WINDOW owns: it has the tabs and the settings, not us.</summary>
        internal event Action<EditorMenuCommand>? MenuCommand;

        // The language the card was built in: its captions are plain text, so a language switch rebuilds it.
        private string? _menuLanguage;

        /// <summary>
        /// What the card's header shows: an icon, the file's name and its folder. Supplied by the
        /// window, which knows the tab; with none, the card opens without a header.
        /// </summary>
        internal Func<(FrameworkElement? Icon, string Title, string Meta)>? CardHeaderSource { get; set; }

        /// <summary>
        /// The document card: a header naming the file, then every command as a tile with its shortcut. Built once;
        /// what applies is set as it opens.
        /// </summary>
        private void BuildMenu()
        {
            var m = KillerShell.Controls.CardMenus.TileCard();

            var cardHead = KillerShell.Controls.CardMenus.Slot();
            cardHead.Tag = "cardhead";
            m.Items.Add(cardHead);
            KillerShell.Controls.CardMenus.WatchKeyboardStart(m);

            // Undo and Redo are wrapped rather than passed as method groups: both return whether there
            // was anything to undo, and the tile is dimmed otherwise. Save, Go to line, wrap, settings
            // and close belong to the WINDOW, which owns the bar and the tabs, so they are raised.
            m.Items.Add(KillerShell.Controls.CardMenus.Tiles(m,
            [
                KillerShell.Controls.CardMenus.Tile("undo", 0xE7A7, "Str_Ed_Undo", "Str_Card_Undo", "Ctrl+Z", () => Undo()),
                KillerShell.Controls.CardMenus.Tile("redo", 0xE7A6, "Str_Ed_Redo", "Str_Card_Redo", "Ctrl+Y", () => Redo()),
                KillerShell.Controls.CardMenus.Tile("save", 0xE74E, "Str_Ed_Save", "Str_Card_Save", "Ctrl+S",
                    () => MenuCommand?.Invoke(EditorMenuCommand.Save)),
                KillerShell.Controls.CardMenus.Tile("cut", 0xE8C6, "Str_Ed_Cut", "Str_Card_Cut", "Ctrl+X", Cut),
                KillerShell.Controls.CardMenus.Tile("copy", 0xE8C8, "Str_Ed_Copy", "Str_Card_Copy", "Ctrl+C", Copy),
                KillerShell.Controls.CardMenus.Tile("paste", 0xE77F, "Str_Ed_Paste", "Str_Card_Paste", "Ctrl+V", Paste),
                KillerShell.Controls.CardMenus.Tile("selectall", 0xE8B3, "Str_Ed_SelectAll", "Str_Card_SelectAll", "Ctrl+A", SelectAll),
                KillerShell.Controls.CardMenus.Tile("find", 0xE721, "Str_Ed_Find", null, "Ctrl+F", () => Find.Open()),
                KillerShell.Controls.CardMenus.Tile("goto", 0xE8A1, "Str_Ed_Goto", null, "Ctrl+G",
                    () => MenuCommand?.Invoke(EditorMenuCommand.GoToLine)),
                KillerShell.Controls.CardMenus.Tile("wrap", 0x21B5, "Str_Ed_OptWrap", null, "Alt+Z",
                    () => MenuCommand?.Invoke(EditorMenuCommand.ToggleWrap), on: WordWrap),
                KillerShell.Controls.CardMenus.Tile("settings", 0xE713, "Str_Ed_Settings", null, null,
                    () => MenuCommand?.Invoke(EditorMenuCommand.Settings)),
                KillerShell.Controls.CardMenus.Tile("closetab", 0xE8BB, "Str_Ed_CloseTab", null, "Ctrl+W",
                    () => MenuCommand?.Invoke(EditorMenuCommand.CloseTab)),
            ]));

            // Availability is decided as it OPENS, not once at build time: a lit tile that does nothing
            // is worse than a dim one, and these change every keystroke. The header is about the file as
            // it is NOW, saved under a new name or not, so it is filled on every opening too.
            m.Opened += (_, _) =>
            {
                var source = CardHeaderSource?.Invoke();
                cardHead.Visibility = source.HasValue ? Visibility.Visible : Visibility.Collapsed;
                if (source is { } head) KillerShell.Controls.CardMenus.SetHeader(cardHead, head.Icon, head.Title, head.Meta);

                bool selected = TextArea.Selection?.Length > 0;
                KillerShell.Controls.CardMenus.SetTile(m, "undo", CanUndo);
                KillerShell.Controls.CardMenus.SetTile(m, "redo", CanRedo);
                KillerShell.Controls.CardMenus.SetTile(m, "cut", selected);
                KillerShell.Controls.CardMenus.SetTile(m, "copy", selected);
                KillerShell.Controls.CardMenus.SetTile(m, "wrap", true, WordWrap);
            };

            ContextMenu = m;
            _menuLanguage = KillerShell.Controls.CardMenus.Loc("Str_Card_Actions");
        }

        /// <summary>Rebuilds the card when the language changed since it was built.</summary>
        protected override void OnContextMenuOpening(ContextMenuEventArgs e)
        {
            RefreshMenuLanguage();
            base.OnContextMenuOpening(e);
        }

        private void RefreshMenuLanguage()
        {
            if (_menuLanguage != KillerShell.Controls.CardMenus.Loc("Str_Card_Actions")) BuildMenu();
        }
    }

    /// <summary>Menu rows the document cannot carry out on its own.</summary>
    internal enum EditorMenuCommand
    {
        GoToLine,
        ToggleWrap,
        Save,
        Settings,
        CloseTab,
    }
}
