using System;
using System.Windows;
using System.Windows.Controls;

// The document's right-click menu. Part of EditorControl.
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

        private MenuItem? _undoItem, _redoItem, _cutItem, _copyItem, _wrapItem;

        // Glyphs as codepoints, never literal private-use characters: these files stay BOM-less
        // UTF-8 with zero non-ASCII bytes, and typing one directly is what made KillerPDF's
        // release.ps1 PS7-only.
        private static string Glyph(int cp) => ((char)cp).ToString();

        private void BuildMenu()
        {
            var m = new ContextMenu();

            // Wrapped rather than passed as method groups: both return a bool saying whether
            // there was anything to undo, and the row already knows there was - the menu
            // disables them otherwise.
            _undoItem = Row(m, "Str_Ed_Undo", Glyph(0xE7A7), "Ctrl+Z", () => Undo());
            _redoItem = Row(m, "Str_Ed_Redo", Glyph(0xE7A6), "Ctrl+Y", () => Redo());

            m.Items.Add(new Separator());

            _cutItem  = Row(m, "Str_Ed_Cut",   Glyph(0xE8C6), "Ctrl+X", Cut);
            _copyItem = Row(m, "Str_Ed_Copy",  Glyph(0xE8C8), "Ctrl+C", Copy);
            Row(m, "Str_Ed_Paste",     Glyph(0xE77F), "Ctrl+V", Paste);
            Row(m, "Str_Ed_SelectAll", Glyph(0xE8B3), "Ctrl+A", SelectAll);

            m.Items.Add(new Separator());

            Row(m, "Str_Ed_Find", Glyph(0xE721), "Ctrl+F", () => Find.Open());
            Row(m, "Str_Ed_Goto", Glyph(0xE8A1), "Ctrl+G",
                () => MenuCommand?.Invoke(EditorMenuCommand.GoToLine));

            _wrapItem = Row(m, "Str_Ed_OptWrap", Glyph(0x21B5), null,
                () => MenuCommand?.Invoke(EditorMenuCommand.ToggleWrap));

            m.Items.Add(new Separator());

            Row(m, "Str_Ed_Save", Glyph(0xE74E), "Ctrl+S",
                () => MenuCommand?.Invoke(EditorMenuCommand.Save));
            Row(m, "Str_Ed_Settings", Glyph(0xE713), null,
                () => MenuCommand?.Invoke(EditorMenuCommand.Settings));

            m.Items.Add(new Separator());
            Row(m, "Str_Ed_CloseTab", Glyph(0xE8BB), "Ctrl+W",
                () => MenuCommand?.Invoke(EditorMenuCommand.CloseTab));

            // Availability is decided as it OPENS, not once at build time: a lit row that does
            // nothing is worse than a dim one, and all four of these change every keystroke.
            m.Opened += (_, _) =>
            {
                if (_undoItem != null) _undoItem.IsEnabled = CanUndo;
                if (_redoItem != null) _redoItem.IsEnabled = CanRedo;
                if (_cutItem  != null) _cutItem.IsEnabled  = TextArea.Selection?.Length > 0;
                if (_copyItem != null) _copyItem.IsEnabled = TextArea.Selection?.Length > 0;
                if (_wrapItem != null) _wrapItem.IsChecked = WordWrap;
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

            var icon = new TextBlock { Text = glyph };
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
