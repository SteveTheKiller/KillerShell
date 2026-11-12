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

        private MenuItem? _wrapItem;
        private Button? _undoButton, _redoButton, _cutButton, _copyButton;

        /// <summary>
        /// What the card's header shows: an icon, the file's name and its folder. Supplied by the
        /// window, which knows the tab; with none, the card opens without a header.
        /// </summary>
        internal Func<(FrameworkElement? Icon, string Title, string Meta)>? CardHeaderSource { get; set; }

        // Glyphs as codepoints, never literal private-use characters: these files stay BOM-less
        // UTF-8 with zero non-ASCII bytes, and typing one directly is what made KillerPDF's
        // release.ps1 PS7-only.
        private static string Glyph(int cp) => ((char)cp).ToString();

        /// <summary>
        /// The document card: a header naming the file, the six everyday edits as a row of
        /// buttons, then Find, Go to line, wrap, settings and close as rows.
        /// </summary>
        private void BuildMenu()
        {
            var m = KillerShell.Controls.CardMenus.Card();

            var cardHead = KillerShell.Controls.CardMenus.Slot();
            cardHead.Tag = "cardhead";
            m.Items.Add(cardHead);

            // Wrapped rather than passed as method groups: both return a bool saying whether
            // there was anything to undo, and the button already knows there was - the card
            // dims them otherwise.
            var quick = KillerShell.Controls.CardMenus.QuickStrip(out var strip);
            quick.Tag = "cardquick";
            _undoButton = KillerShell.Controls.CardMenus.QuickButton(strip, m, 0xE7A7, "Str_Card_Undo", "Str_Ed_Undo", "Ctrl+Z", () => Undo());
            _redoButton = KillerShell.Controls.CardMenus.QuickButton(strip, m, 0xE7A6, "Str_Card_Redo", "Str_Ed_Redo", "Ctrl+Y", () => Redo());
            _cutButton  = KillerShell.Controls.CardMenus.QuickButton(strip, m, 0xE8C6, "Str_Card_Cut",  "Str_Ed_Cut",  "Ctrl+X", Cut);
            _copyButton = KillerShell.Controls.CardMenus.QuickButton(strip, m, 0xE8C8, "Str_Card_Copy", "Str_Ed_Copy", "Ctrl+C", Copy);
            KillerShell.Controls.CardMenus.QuickButton(strip, m, 0xE77F, "Str_Ed_Paste", "Str_Ed_Paste", "Ctrl+V", Paste);
            // Captions are the card's own short keys (Str_Card_*), hyphenated where a word is too
            // wide for a sixth of the card; the tooltip and the screen reader get the full name.
            // Save belongs to the WINDOW - it owns the bar that reports it - so it is raised.
            KillerShell.Controls.CardMenus.QuickButton(strip, m, 0xE74E, "Str_Card_Save", "Str_Ed_Save", "Ctrl+S",
                () => MenuCommand?.Invoke(EditorMenuCommand.Save));
            // No separator under the strip: its own bottom rule is that line already.
            m.Items.Add(quick);

            Row(m, "Str_Ed_SelectAll", Glyph(0xE8B3), "Ctrl+A", SelectAll);
            Row(m, "Str_Ed_Find", Glyph(0xE721), "Ctrl+F", () => Find.Open());
            Row(m, "Str_Ed_Goto", Glyph(0xE8A1), "Ctrl+G",
                () => MenuCommand?.Invoke(EditorMenuCommand.GoToLine));

            _wrapItem = Row(m, "Str_Ed_OptWrap", Glyph(0x21B5), "Alt+Z",
                () => MenuCommand?.Invoke(EditorMenuCommand.ToggleWrap));

            m.Items.Add(new Separator());

            Row(m, "Str_Ed_Settings", Glyph(0xE713), null,
                () => MenuCommand?.Invoke(EditorMenuCommand.Settings));
            Row(m, "Str_Ed_CloseTab", Glyph(0xE8BB), "Ctrl+W",
                () => MenuCommand?.Invoke(EditorMenuCommand.CloseTab));

            // Availability is decided as it OPENS, not once at build time: a lit button that
            // does nothing is worse than a dim one, and all four of these change every
            // keystroke. The header is about the file as it is NOW, saved under a new name or
            // not, so it is filled on every opening too.
            m.Opened += (_, _) =>
            {
                var source = CardHeaderSource?.Invoke();
                cardHead.Visibility = source.HasValue ? Visibility.Visible : Visibility.Collapsed;
                if (source is { } head) KillerShell.Controls.CardMenus.SetHeader(cardHead, head.Icon, head.Title, head.Meta);

                _undoButton?.IsEnabled = CanUndo;
                _redoButton?.IsEnabled = CanRedo;
                _cutButton?.IsEnabled  = TextArea.Selection?.Length > 0;
                _copyButton?.IsEnabled = TextArea.Selection?.Length > 0;
                _wrapItem?.IsChecked = WordWrap;
            };

            ContextMenu = m;
        }

        /// <summary>
        /// One row. The header is a resource reference so a language switch repaints the menu in
        /// place, the same as every other menu in the app.
        /// </summary>
        private static MenuItem Row(ContextMenu m, string key, string glyph, string? gesture, Action go)
        {
            var item = new MenuItem { InputGestureText = gesture ?? string.Empty };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);

            var icon = new Glyph { Text = glyph };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            item.Icon = icon;

            item.Click += (_, _) => go();
            m.Items.Add(item);
            return item;
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
