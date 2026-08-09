using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Editing;
using KillerShell.Models;

// The document bar: the strip across the top of an editor tab, and the gear behind it.
// Partial of MainWindow.
//
// It REPLACES the folder location row rather than sitting under it. Back, forward, up, the
// address box, the sort menu and the view toggles are all about a listing; over an open file
// they are chrome you have to read past to reach the two things you wanted (PaneBars.cs makes
// the swap).
//
// The split is verbs on the bar, preferences behind the gear. Save, undo, redo, find, go to line
// and wrap are things you DO to the document and belong under the pointer; line numbers, indent
// and font size are things you decide once and then stop thinking about, and a bar wide enough
// to hold both is a bar that starts shedding buttons on a split window.
//
// Every setting behind the gear is app-wide and applies to every open document at once
// (Editing/EditorOptions.cs) - they describe how the user reads code, not what one file is.
//
// One bar per PANE rather than per tab, like the location row it stands in for: the bar belongs
// to the pane and shows whatever document that pane is currently displaying.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // ═══════════════════════════════════════════════════════════
        //  PAINT
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Repaint the bar for <paramref name="t"/>, in whichever pane is showing it.
        /// </summary>
        /// <remarks>
        /// The pane is looked up from the CONTROL rather than assumed to be the focused one: the
        /// dirty flag flips on a keystroke, and with two panes open the pane being typed in is
        /// not always the pane that has focus by the time the event lands.
        /// </remarks>
        private void SyncEditorBar(SearchTab t)
        {
            if (t.Editor == null) return;

            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.EditorSlot.Content, t.Editor));
            if (pane == null) return;

            var editor = t.Editor;
            // An untitled document has no path to show yet, and an empty strip where the path
            // goes reads as a bug. It fills in for real the moment the first save picks a name.
            pane.EditorPathText.Text     = editor.IsUntitled ? Loc("Str_Ed_Untitled") : editor.FilePath;

            // Click-to-edit works for an UNTITLED document too now - typing a path is the
            // save-as (EdPath_Click) - so the cursor and tooltip invite the click either way
            // (Steve, 2026-08-09).
            pane.EditorPathText.Cursor  = Cursors.IBeam;
            pane.EditorPathText.ToolTip = Loc("Str_TT_EdPath");

            pane.EditorEncodingBtn.Content = editor.EncodingLabel;
            pane.EditorEolText.Text        = editor.NewLineLabel;
            SyncEditorEncodingMenu(pane, editor.EncodingLabel);

            // Tag="on" is what SurfaceButton's trigger reads to light a button in the accent -
            // the same signal the tab title's dot carries, so the two never disagree.
            pane.EditorSaveBtn.Tag = editor.Dirty ? "on" : null;
            pane.EditorWrapBtn.Tag = EditorOptions.WordWrap ? "on" : null;

            // Dim rather than hidden: a greyed Undo says "nothing to undo yet", where a button
            // that comes and goes makes the two beside it move under the pointer.
            pane.EditorUndoBtn.IsEnabled = editor.CanUndo;
            pane.EditorRedoBtn.IsEnabled = editor.CanRedo;

            SyncEditorGear(pane);
        }

        /// <summary>The gear's own state. Split out because the popup can be open on its own.</summary>
        private static void SyncEditorGear(FilePane pane)
        {
            pane.EdOptLineNumbers.Tag = EditorOptions.LineNumbers   ? "on" : null;
            pane.EdOptCurrentLine.Tag = EditorOptions.CurrentLine   ? "on" : null;
            pane.EdOptWhitespace.Tag  = EditorOptions.Whitespace    ? "on" : null;
            pane.EdOptSpaces.Tag      = EditorOptions.SpacesForTabs ? "on" : null;

            pane.EdIndent2.Tag = EditorOptions.IndentSize == 2 ? "on" : null;
            pane.EdIndent4.Tag = EditorOptions.IndentSize == 4 ? "on" : null;
            pane.EdIndent8.Tag = EditorOptions.IndentSize == 8 ? "on" : null;
        }

        /// <summary>Lights whichever row in the encoding popup matches the document's own label.</summary>
        private static void SyncEditorEncodingMenu(FilePane pane, string label)
        {
            pane.EdEncUtf8.Tag    = label == "UTF-8"     ? "on" : null;
            pane.EdEncUtf8Bom.Tag = label == "UTF-8 BOM" ? "on" : null;
            pane.EdEncUtf16Le.Tag = label == "UTF-16 LE" ? "on" : null;
            pane.EdEncUtf16Be.Tag = label == "UTF-16 BE" ? "on" : null;
            pane.EdEncAnsi.Tag    = label == "ANSI"      ? "on" : null;
        }

        /// <summary>
        /// Push the settings onto every open document and repaint both panes' bars.
        /// </summary>
        /// <remarks>
        /// Applied to the tabs that are already open rather than only to the next one, the same
        /// way the font slots behave (Fonts.cs). A preference that only takes effect in a tab you
        /// have not opened yet reads as broken.
        /// </remarks>
        private void ApplyEditorOptions()
        {
            EditorOptions.Save();

            foreach (var p in new[] { LeftPane, RightPane })
                foreach (var tab in p.Tabs)
                    if (tab.Editor != null) EditorOptions.Apply(tab.Editor);

            foreach (var p in LivePanes())
                if (p.Active?.Editor != null) SyncEditorBar(p.Active);
        }

        // ═══════════════════════════════════════════════════════════
        //  THE VERBS
        // ═══════════════════════════════════════════════════════════
        /// <summary>The document the bar is acting on, or null on any other kind of tab.</summary>
        private Editing.EditorControl? BarEditor => _active.Editor;

        internal void EditorSave_Click(object sender, RoutedEventArgs e) => SaveActiveEditor();

        internal void EditorUndo_Click(object sender, RoutedEventArgs e)
        {
            var ed = BarEditor;
            if (ed == null || !ed.CanUndo) return;
            ed.Undo();
            ed.TextArea.Focus();          // the caret belongs back in the text, not on the button
            SyncEditorBar(_active);
        }

        internal void EditorRedo_Click(object sender, RoutedEventArgs e)
        {
            var ed = BarEditor;
            if (ed == null || !ed.CanRedo) return;
            ed.Redo();
            ed.TextArea.Focus();
            SyncEditorBar(_active);
        }

        /// <summary>Open AvalonEdit's own find bar - the same one Ctrl+F opens in the document.</summary>
        internal void EditorFind_Click(object sender, RoutedEventArgs e)
        {
            var ed = BarEditor;
            if (ed == null) return;
            ed.TextArea.Focus();
            ed.Find.Open();
        }

        /// <summary>Go to line: a popup with one box, opened from the bar or from Ctrl+G.</summary>
        internal void EditorGoto_Click(object sender, RoutedEventArgs e)
        {
            var ed = BarEditor;
            if (ed == null) return;

            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.EditorSlot.Content, ed));
            if (pane == null) return;

            // Seeded with the line the caret is already on and selected, so the common case is
            // type-a-number-and-Enter with nothing to clear first.
            pane.EditorGotoBox.Text = ed.TextArea.Caret.Line.ToString(CultureInfo.InvariantCulture);
            pane.EditorGotoPopup.IsOpen = true;
            if (pane.EditorGotoPopup.Child is UIElement gotoChild) Anim.FadeIn(gotoChild);
            pane.EditorGotoBox.SelectAll();
            pane.EditorGotoBox.Focus();
        }

        internal void EditorGotoBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not TextBox box) return;

            if (e.Key == System.Windows.Input.Key.Escape)
            {
                ClosePopupOf(box);
                BarEditor?.TextArea.Focus();
                e.Handled = true;
                return;
            }
            if (e.Key != System.Windows.Input.Key.Enter) return;

            e.Handled = true;
            var ed = BarEditor;
            if (ed == null) return;

            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int line))
            {
                // Clamped rather than refused. "999999" means the end of the file, which is what
                // the person typing it wanted, and an error message for it would be pedantry.
                line = Math.Max(1, Math.Min(ed.Document.LineCount, line));
                ed.ScrollToLine(line);
                ed.TextArea.Caret.Line   = line;
                ed.TextArea.Caret.Column = 1;
            }

            ClosePopupOf(box);
            ed.TextArea.Focus();
        }

        // The box lives inside the popup, so the popup is its ancestor rather than a field this
        // method could name - and there is one per pane.
        private static void ClosePopupOf(DependencyObject child)
        {
            var d = child;
            while (d != null)
            {
                if (d is System.Windows.Controls.Primitives.Popup p) { p.IsOpen = false; return; }
                d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
        }

        /// <summary>Word wrap. Persisted and pushed onto every open document.</summary>
        internal void EditorWrap_Click(object sender, RoutedEventArgs e)
        {
            if (BarEditor == null) return;

            EditorOptions.WordWrap = !EditorOptions.WordWrap;
            ApplyEditorOptions();
            SetTabStatusKey(_active, EditorOptions.WordWrap ? "Str_Ed_WrapOn" : "Str_Ed_WrapOff");
        }

        // ═══════════════════════════════════════════════════════════
        //  PATH: click to rename/move (mirrors TerminalBar.cs TermCwd_Click)
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Click the document's path to edit it in place - type a path and press Enter to rename
        /// and/or move the file on disk. Only wired up once there IS a real file: an untitled
        /// document has never been saved, so there is nothing yet to rename (SyncEditorBar gates
        /// the cursor/tooltip on the same check, this is the belt-and-braces guard against a
        /// stray click landing between the two).
        /// </summary>
        internal void EdPath_Click(object sender, MouseButtonEventArgs e)
        {
            var t = _active;
            if (t.Editor == null) return;
            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.EditorSlot.Content, t.Editor));
            if (pane == null) return;

            // An UNTITLED document is editable here too now (Steve, 2026-08-09: "the filename
            // in the addressbar of the text editor doesnt let me edit"): typing a path IS the
            // save-as, so the box prefills with the tab's folder and a placeholder name to
            // overtype instead of refusing the click.
            pane.EditorPathBox.Text = t.Editor.IsUntitled
                ? Path.Combine(string.IsNullOrEmpty(t.CurrentFolder) ? HomeFolder : t.CurrentFolder, "untitled.txt")
                : t.Editor.FilePath;
            pane.EditorPathText.Visibility = Visibility.Collapsed;
            pane.EditorPathBox.Visibility = Visibility.Visible;
            pane.EditorPathBox.Focus();
            pane.EditorPathBox.SelectAll();
        }

        private void EndEditEdPath(FilePane pane)
        {
            pane.EditorPathBox.Visibility = Visibility.Collapsed;
            pane.EditorPathText.Visibility = Visibility.Visible;
        }

        /// <summary>Enter commits (renames/moves the file), Escape cancels back to the readout.</summary>
        internal void EditorPathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;
            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.EditorPathBox, box));
            if (pane == null) return;

            if (e.Key == Key.Escape)
            {
                EndEditEdPath(pane);
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            var t = pane.Active;
            if (t == null) { EndEditEdPath(pane); return; }
            var editor = t.Editor;
            if (editor == null) { EndEditEdPath(pane); return; }

            string newPath = box.Text.Trim();
            if (!editor.IsUntitled && string.Equals(newPath, editor.FilePath, StringComparison.Ordinal))
            { EndEditEdPath(pane); return; }   // genuinely unchanged

            string? dir = Path.GetDirectoryName(newPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                // Stays in edit mode rather than reverting - same convention as the shell's cwd
                // box (TerminalBar.cs): a typo is worth the chance to fix, not a round trip back
                // through click-to-edit.
                SetTabStatusKey(t, "Str_Status_EdPathNotFound", newPath);
                return;
            }

            // A case-only rename ("readme.txt" -> "README.txt") is a real rename and has to skip
            // the exists check, the same carve-out FileOps.Rename makes: on a case-insensitive
            // volume that check would find the very file being renamed and refuse it. Never
            // applies to an untitled document, which has no original to case-rename.
            bool caseOnly = !editor.IsUntitled
                         && string.Equals(newPath, editor.FilePath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
            {
                SetTabStatusKey(t, "Str_Status_RenameFailed", "already exists");
                return;
            }

            if (editor.IsUntitled)
            {
                // Committing a path on an UNTITLED document IS the save-as: adopt the name,
                // then write the buffer there. On a failed write it stays in edit mode with the
                // path already adopted, so a re-Enter simply retries.
                editor.AdoptPath(newPath, editor.CurrentEncoding);
                if (!editor.SaveFile(out string saveError, out _))
                {
                    SetTabStatusKey(t, "Str_Status_RenameFailed", saveError);
                    return;
                }
            }
            else
            {
                try { File.Move(editor.FilePath, newPath); }
                catch (Exception ex)
                {
                    // File.Move throws before touching the source when the destination side of
                    // the move fails, so the original is untouched here and the tab is still
                    // correctly pointing at it - nothing below this runs.
                    SetTabStatusKey(t, "Str_Status_RenameFailed", ex.Message);
                    return;
                }

                // Same encoding, kept - a rename is not a re-save, so AdoptPath's re-highlight
                // from the (possibly new) extension is the only thing that should change here.
                editor.AdoptPath(newPath, editor.CurrentEncoding);
            }

            // Same follow-up SaveActiveEditor makes after a first Save As: the folder backing
            // this tab has moved, so the address row and nav buttons have to move with it.
            t.CurrentFolder = Path.GetDirectoryName(newPath) ?? string.Empty;
            t.RootPath       = t.CurrentFolder;

            SetEditorTitle(t);     // EditorTabs.cs - the tab's file name follows the new path
            SyncEditorBar(t);      // the bar's own readout follows too
            EndEditEdPath(pane);
        }

        /// <summary>Clicking away without pressing Enter cancels rather than commits.</summary>
        internal void EditorPathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box) return;
            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.EditorPathBox, box));
            if (pane != null) EndEditEdPath(pane);
        }

        // ═══════════════════════════════════════════════════════════
        //  THE GEAR
        // ═══════════════════════════════════════════════════════════
        internal void EditorGear_Click(object sender, RoutedEventArgs e)
        {
            if (BarEditor == null) return;
            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.EditorSlot.Content, BarEditor));
            if (pane == null) return;

            SyncEditorGear(pane);
            pane.EditorGearPopup.IsOpen = !pane.EditorGearPopup.IsOpen;
            if (pane.EditorGearPopup.IsOpen && pane.EditorGearPopup.Child is UIElement gearChild) Anim.FadeIn(gearChild);
        }

        internal void EdOptLineNumbers_Click(object sender, RoutedEventArgs e)
        {
            EditorOptions.LineNumbers = !EditorOptions.LineNumbers;
            ApplyEditorOptions();
        }

        internal void EdOptCurrentLine_Click(object sender, RoutedEventArgs e)
        {
            EditorOptions.CurrentLine = !EditorOptions.CurrentLine;
            ApplyEditorOptions();
        }

        internal void EdOptWhitespace_Click(object sender, RoutedEventArgs e)
        {
            EditorOptions.Whitespace = !EditorOptions.Whitespace;
            ApplyEditorOptions();
        }

        internal void EdOptSpaces_Click(object sender, RoutedEventArgs e)
        {
            EditorOptions.SpacesForTabs = !EditorOptions.SpacesForTabs;
            ApplyEditorOptions();
        }

        // The size rides on CommandParameter, not on Tag: Tag is spoken for by SurfaceButton's
        // lit trigger, which is what shows which of the three is current (same split as the sort
        // menu, Results.cs).
        internal void EdIndent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string s) return;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size)) return;

            EditorOptions.IndentSize = EditorOptions.ClampIndent(size);
            ApplyEditorOptions();
        }

        /// <summary>The last gear row: hands off to the Fonts dialog, which owns both font slots.</summary>
        internal void EdOptFonts_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in LivePanes()) p.EditorGearPopup.IsOpen = false;
            FontsRow_Click(this, new RoutedEventArgs());     // Fonts.cs
        }

        // ═══════════════════════════════════════════════════════════
        //  THE ENCODING PICKER
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Open (or close) the encoding readout's own popup - same shape as the gear
        /// (EditorGear_Click): anchored to the button that was clicked, not a fixed point.
        /// </summary>
        internal void EditorEncoding_Click(object sender, RoutedEventArgs e)
        {
            if (BarEditor == null) return;
            var pane = LivePanes().FirstOrDefault(p => ReferenceEquals(p.EditorSlot.Content, BarEditor));
            if (pane == null) return;

            SyncEditorEncodingMenu(pane, BarEditor.EncodingLabel);
            pane.EditorEncodingPopup.IsOpen = !pane.EditorEncodingPopup.IsOpen;
            if (pane.EditorEncodingPopup.IsOpen && pane.EditorEncodingPopup.Child is UIElement encChild) Anim.FadeIn(encChild);
        }

        /// <summary>
        /// A row in the encoding popup was picked. Changes what the NEXT save writes
        /// (EditorControl.SetEncoding) - a single "change encoding" action rather than VS Code's
        /// separate reopen/save-with-encoding pair, which is more subsystem than a first pass
        /// needs. The five keys mirror the standard set VS Code itself defaults to.
        /// </summary>
        internal void EdEncoding_Click(object sender, RoutedEventArgs e)
        {
            var ed = BarEditor;
            if (ed == null || sender is not Button b || b.CommandParameter is not string key) return;

            Encoding? encoding = key switch
            {
                "utf8"     => new UTF8Encoding(false),
                "utf8bom"  => new UTF8Encoding(true),
                "utf16le"  => new UnicodeEncoding(false, true),
                "utf16be"  => new UnicodeEncoding(true, true),
                "ansi"     => Encoding.Default,
                _          => null,
            };
            if (encoding == null) return;

            ed.SetEncoding(encoding);
            SyncEditorBar(_active);

            foreach (var p in LivePanes()) p.EditorEncodingPopup.IsOpen = false;
        }
    }
}
