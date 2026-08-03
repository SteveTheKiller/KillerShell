using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

// The control behind an editor tab: vendored AvalonEdit, dressed in the app theme.
//
// A subclass rather than a UserControl wrapping one. Everything this adds is about the FILE -
// which one, in what encoding, and whether it still matches disk - and none of it needs a
// visual layer of its own; a wrapper would only give focus and theming one more element to
// travel through.
//
// The encoding handling is the part worth reading twice. AvalonEdit's own Load/Save pair hands
// back Encoding.UTF8 for a file that has NO byte order mark, and that instance writes one - so
// a plain .txt or .bat would silently grow three bytes at the front the first time it was saved
// here. A BOM is a decision about somebody else's file: preserved when it is there, never added
// when it is not.
using KillerShell.Shell;

namespace KillerShell.Editing
{
    internal sealed partial class EditorControl : TextEditor
    {
        /// <summary>
        /// The file this tab is editing, or empty on a document that has never been saved.
        /// </summary>
        /// <remarks>
        /// Empty is the ONLY thing that makes a document untitled, and it settles on a real path
        /// the first time it is saved (MainWindow.PromptSaveAs). Every other way in - a result
        /// row, the profile, a command-line path - arrives with the path already known.
        /// </remarks>
        internal string FilePath { get; private set; }

        /// <summary>True on a blank document that has not been given a path yet.</summary>
        internal bool IsUntitled => FilePath.Length == 0;

        /// <summary>
        /// True only for a document opened via Ctrl+F7 (MainWindow.NewDocumentAdmin,
        /// EditorTabs.cs). When a save on THIS tab fails with access denied, SaveActiveEditor
        /// offers an elevated retry (Elevation.cs RetrySaveElevated) instead of just reporting
        /// the failure. A document opened via plain F7 or by opening an existing file leaves
        /// this false, so a permissions error there still fails normally rather than silently
        /// attempting elevation.
        /// </summary>
        internal bool ElevatedSaveOnFail { get; set; }

        /// <summary>This editor's find bar, installed once in the constructor.</summary>
        internal ICSharpCode.AvalonEdit.Search.SearchPanel Find { get; }

        /// <summary>
        /// Raised when Ctrl+wheel changed the size, so the window can push the new one onto
        /// every other open document and repaint the gear's readout.
        /// </summary>
        internal event Action? ZoomChanged;

        /// <summary>Encoding as it was found on disk, written back unchanged on save.</summary>
        private Encoding _encoding = new UTF8Encoding(false);

        /// <summary>
        /// This document's encoding, for a caller that has to hand it back through AdoptPath -
        /// a rename/move (EditorBar.cs EditorPathBox_KeyDown) keeps the file's existing encoding
        /// rather than resetting it, the same way Save As's own AdoptPath call does not change it.
        /// </summary>
        internal Encoding CurrentEncoding => _encoding;

        /// <summary>
        /// What the bar shows for the encoding: "UTF-8", "UTF-8 BOM", "UTF-16 LE", "ANSI".
        /// </summary>
        /// <remarks>
        /// Worth a readout rather than being left implicit. PowerShell 5.1 reads a BOM-less file
        /// as the system ANSI codepage, so on a script the difference between the first two is
        /// the difference between a prompt full of box drawing and a prompt full of mojibake -
        /// and it is invisible in the text itself.
        /// </remarks>
        internal string EncodingLabel { get; private set; } = "UTF-8";

        /// <summary>What the bar shows for the line ending: "CRLF", "LF", "CR" or "-".</summary>
        internal string NewLineLabel { get; private set; } = "-";

        /// <summary>Raised when the unsaved-changes state flips, so the tab title can follow.</summary>
        internal event Action? DirtyChanged;

        private bool _wasDirty;

        internal EditorControl(string path)
        {
            FilePath = path;

            Options.AllowScrollBelowDocument = true;

            // Font, wrap, line numbers, indent and whitespace all come from one place, so the
            // gear, the Fonts dialog, the bar and a newly opened tab can never be showing four
            // different answers.
            EditorOptions.Apply(this);

            // Off deliberately. A script full of URLs would come back underlined in the link
            // color, which fights whatever the syntax theme is doing two characters away - and
            // a stray Ctrl+click in a file you are editing should not open a browser.
            Options.EnableHyperlinks      = false;
            Options.EnableEmailHyperlinks = false;

            // AvalonEdit's own find bar on Ctrl+F. The window's Ctrl+F opens the results
            // filter, which means nothing over a document, so the key handler hands the chord
            // over whenever the editor has focus (EditorTabs.cs).
            //
            // The panel is KEPT rather than installed and forgotten: Install attaches a fresh
            // one every call, so the bar's find button asking for a second would wire a second
            // set of handlers onto the same text area.
            Find = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(this);

            // Assigned only when there IS one: the property is declared non-nullable and its
            // default already is null, so pushing a missing definition through it would be a
            // warning that buys nothing.
            var highlighting = ForExtension(path);
            if (highlighting != null) SyntaxHighlighting = highlighting;

            ApplyTheme();
            BuildMenu();          // EditorMenu.cs - right-click had nothing at all before this
            TextChanged += (_, _) => ReportDirty();
        }

        // ═══════════════════════════════════════════════════════════
        //  DISK
        // ═══════════════════════════════════════════════════════════
        /// <summary>Read the file in. Never throws; the caller reports what went wrong.</summary>
        internal bool LoadFile(out string error)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(FilePath);
                _encoding = Detect(bytes, out int skip);
                Text = _encoding.GetString(bytes, skip, bytes.Length - skip);

                EncodingLabel = Describe(_encoding);
                NewLineLabel  = DescribeNewLine(Text);

                // Opening a file is not an edit. Without this the first Ctrl+Z would undo the
                // load itself and leave an empty document over a file with contents in it.
                Document.UndoStack.ClearAll();
                IsModified  = false;
                _wasDirty   = false;
                CaretOffset = 0;

                error = string.Empty;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>Fill the document from a string rather than from disk, for --demo.</summary>
        /// <remarks>
        /// The real loader reads bytes and sniffs the encoding off them, and a fabricated path
        /// has no bytes to sniff. Highlighting is already chosen in the constructor from the
        /// extension, so a demo document lights up exactly as the real one would. Never marked
        /// dirty: a screenshot should not catch the unsaved dot on a file nobody edited.
        /// </remarks>
        internal void LoadDemo(string text)
        {
            _encoding     = new UTF8Encoding(false);
            Text          = text;
            EncodingLabel = Describe(_encoding);
            NewLineLabel  = DescribeNewLine(Text);
            Document.UndoStack.ClearAll();   // as in LoadFile - Ctrl+Z must not blank the document
            IsModified    = false;
            _wasDirty     = false;
            CaretOffset   = 0;
        }

        /// <summary>Start an empty, never-saved document. Nothing to read and nothing to sniff.</summary>
        /// <remarks>
        /// Kept apart from LoadFile rather than folded into it as an "empty path" case: LoadFile
        /// reports failure through an out parameter that every caller has to handle, and there
        /// is no way for this to fail.
        /// </remarks>
        internal void LoadEmpty()
        {
            _encoding     = new UTF8Encoding(false);
            EncodingLabel = Describe(_encoding);
            NewLineLabel  = DescribeNewLine(Text);
            Document.UndoStack.ClearAll();
            IsModified    = false;
            _wasDirty     = false;
        }

        /// <summary>
        /// Give an untitled document the path it was just saved to, and the encoding to write
        /// it in. Called from the Save As prompt (MainWindow.PromptSaveAs).
        /// </summary>
        /// <remarks>
        /// Re-runs the highlighting lookup, because the extension is only known now: a blank
        /// buffer saved as .ps1 has to come back colored, or the one thing that says the app has
        /// an editor in it rather than a text box would only ever work on a file opened from a
        /// row. ApplyTheme rather than a bare assignment - the shipped .xshd colors are written
        /// for a white editor and have to be lifted onto this pane (EditorHighlighting).
        /// </remarks>
        internal void AdoptPath(string path, Encoding encoding)
        {
            FilePath      = path;
            _encoding     = encoding;
            EncodingLabel = Describe(encoding);

            var highlighting = ForExtension(path);
            if (highlighting != null) SyntaxHighlighting = highlighting;
            ApplyTheme();
        }

        /// <summary>
        /// Change this document's ACTIVE encoding - what the next save writes - without touching
        /// the text already loaded into it. Wired to the encoding readout's new picker
        /// (EditorBar.cs EdEncoding_Click).
        /// </summary>
        /// <remarks>
        /// Single-mode by design: this is "reopen" and "save as" folded into one, the simpler
        /// half of what VS Code offers as two separate menus. The in-memory text is not
        /// re-decoded from disk, so switching away from a lossy detection (ANSI on bytes that
        /// were not really UTF-8) does not un-corrupt anything already read wrong - it only
        /// changes what gets written from here on. A no-op pick (the label would come out the
        /// same, e.g. clicking UTF-8 while already on UTF-8) leaves Dirty untouched, the same way
        /// every other bar toggle only marks dirty on a real change.
        /// </remarks>
        internal void SetEncoding(Encoding encoding)
        {
            string label = Describe(encoding);
            if (label == EncodingLabel) return;

            _encoding     = encoding;
            EncodingLabel = label;

            // An unsaved encoding change is a real pending change - the next save writes
            // different bytes than the ones on disk even if not one character of text moved.
            IsModified = true;
            ReportDirty();
        }

        /// <summary>Write the file back in the encoding it was read in.</summary>
        /// <param name="accessDenied">
        /// True when the write failed specifically because access was denied - the one case
        /// SaveActiveEditor (EditorTabs.cs) offers an elevated retry for, gated on
        /// <see cref="ElevatedSaveOnFail"/>. Any other failure (disk full, path gone, file
        /// locked by another process) reports normally, because elevation would not fix it.
        /// </param>
        internal bool SaveFile(out string error, out bool accessDenied)
        {
            accessDenied = false;
            try
            {
                // The encoding instance carries its own preamble, so a file that arrived with a
                // BOM keeps it and one that arrived without stays without.
                File.WriteAllText(FilePath, Text, _encoding);
                IsModified = false;
                ReportDirty();

                error = string.Empty;
                return true;
            }
            catch (UnauthorizedAccessException ex) { accessDenied = true; error = ex.Message; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// Write the current text, in this document's own encoding, to an arbitrary path rather
        /// than <see cref="FilePath"/>. Used only to stage the payload for an elevated save
        /// retry (Elevation.cs RetrySaveElevated) - the elevated helper process then copies that
        /// staged file over the real, permission-denied destination.
        /// </summary>
        internal void ExportTextTo(string path) => File.WriteAllText(path, Text, _encoding);

        /// <summary>
        /// How to read these bytes, and how many bytes of byte order mark to step over.
        /// </summary>
        /// <remarks>
        /// The UTF-8 test uses a THROWING decoder, which is the whole point of it: a lenient one
        /// would swap every byte it did not understand for U+FFFD and the next save would write
        /// that corruption back over the user's file. Encoding.Default is the system ANSI
        /// codepage on net48 - which is what a .bat or a .reg written by an older tool actually
        /// is - and it is only ever reached when the bytes are not valid UTF-8, so plain ASCII
        /// never lands there.
        /// </remarks>
        private static Encoding Detect(byte[] bytes, out int skip)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            { skip = 3; return new UTF8Encoding(true); }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            { skip = 2; return new UnicodeEncoding(false, true); }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            { skip = 2; return new UnicodeEncoding(true, true); }

            skip = 0;
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                return new UTF8Encoding(false);   // a non-throwing twin, for the write back
            }
            catch (DecoderFallbackException) { return Encoding.Default; }
        }

        private static string Describe(Encoding encoding)
        {
            if (encoding is UnicodeEncoding u) return u.GetPreamble().Length > 0 && u.GetPreamble()[0] == 0xFE
                ? "UTF-16 BE" : "UTF-16 LE";
            if (encoding is UTF8Encoding utf8) return utf8.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8";
            return "ANSI";
        }

        /// <summary>
        /// The line ending this file uses, decided by the FIRST one in it.
        /// </summary>
        /// <remarks>
        /// First rather than most common. A mixed file is a real thing - a Windows tool having
        /// appended to something written on Linux - and what matters is what the rest of the
        /// file is going to get when you press Enter, which is what AvalonEdit reads off the
        /// front. Counting would report a majority that nothing actually acts on.
        /// </remarks>
        private static string DescribeNewLine(string text)
        {
            int i = text.IndexOf('\n');
            int r = text.IndexOf('\r');

            if (i < 0 && r < 0) return "-";                    // one line, so nothing to report
            if (i < 0) return "CR";                            // classic Mac, and still out there
            if (r >= 0 && r == i - 1) return "CRLF";
            return "LF";
        }

        /// <summary>The highlighting for this file's extension, or null for plain text.</summary>
        private static IHighlightingDefinition? ForExtension(string path)
        {
            try
            {
                string ext = Path.GetExtension(path);
                if (ext.Length == 0) return null;

                // The formats AvalonEdit does not ship - .bat, .reg, .ini, .yml, .log, .csv -
                // have to be in the manager before it is asked (KillerHighlighting.cs).
                KillerHighlighting.EnsureRegistered();
                return HighlightingManager.Instance.GetDefinitionByExtension(ext);
            }
            catch { return null; }   // a malformed path is the caller's problem, not the theme's
        }

        // ═══════════════════════════════════════════════════════════
        //  DIRTY STATE
        // ═══════════════════════════════════════════════════════════
        /// <summary>True while the document differs from what is on disk.</summary>
        internal bool Dirty => IsModified;

        // Edge-triggered. TextChanged fires on every keystroke and the tab title only has two
        // states, so raising it per character would repaint the strip for nothing.
        private void ReportDirty()
        {
            if (IsModified == _wasDirty) return;
            _wasDirty = IsModified;
            DirtyChanged?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════
        //  THEME
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Take the colors from the live theme. Re-run after a theme or accent switch
        /// (EditorTabs.RefreshEditorThemes).
        /// </summary>
        /// <remarks>
        /// Resolved once into plain brushes rather than bound as dynamic resources, which is how
        /// the terminal does it too (TerminalPalette.For): two of these are not theme keys at
        /// all but the accent at a computed alpha, and a half-bound half-computed surface would
        /// go out of step the moment one of them changed and the other did not.
        /// </remarks>
        internal void ApplyTheme()
        {
            // SurfaceBrush, not PaneBrush (Steve, 2026-08-02) - the same "elevated but not
            // stark" step the terminal now uses (TerminalPalette.cs), so a document reads as a
            // page sunk slightly into the pane rather than flush with it. SurfaceBrush already
            // sits between BackgroundBrush and PaneBrush in every theme.
            Color bg     = Res("SurfaceBrush", Color.FromRgb(0x1E, 0x1E, 0x1E));
            Color fg     = Res("TextBrush",    Color.FromRgb(0xE0, 0xE0, 0xE0));
            Color dim    = Res("DimTextBrush", Color.FromRgb(0x80, 0x80, 0x80));
            Color accent = Res("PrimaryBrush", Color.FromRgb(0x50, 0xAE, 0xE8));

            Background            = new SolidColorBrush(bg);
            Foreground            = new SolidColorBrush(fg);
            LineNumbersForeground = new SolidColorBrush(dim);

            // The little square where the two scrollbars meet. WPF's stock ScrollViewer template
            // fills it from SystemColors.ControlBrushKey - a light gray whatever theme is on, so
            // it read as a white notch cut into the bottom right corner of the editor. AvalonEdit
            // uses the stock template (TextEditor.xaml), so the fix is to answer that lookup from
            // inside the control rather than to retemplate anything.
            Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(bg);

            // The shipped .xshd colors were written for a white editor and several of them sink
            // into these panes. Hue kept, lightness lifted (EditorHighlighting.cs).
            EditorHighlighting.MakeReadable(SyntaxHighlighting, bg);
            TextArea.TextView.Redraw();

            TextArea.SelectionBrush   = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
            TextArea.Caret.CaretBrush = new SolidColorBrush(accent);

            // A wash of the accent rather than a gray. The app has six themes and a fixed gray
            // is either invisible on one end of them or a stripe on the other; the accent is the
            // one color guaranteed to have been picked to sit on this pane.
            TextArea.TextView.CurrentLineBackground =
                new SolidColorBrush(Color.FromArgb(0x18, accent.R, accent.G, accent.B));

            // AvalonEdit draws the current line with a BORDER as well as a background fill, and
            // only the fill was ever themed here - the border stayed at AvalonEdit's own stock
            // default (an olive/green pen, nothing to do with any KillerShell theme) and read as
            // a stray unthemed outline around the caret's line (Steve, 2026-08-02). The wash
            // above already marks the line clearly enough on its own; null turns the border off
            // rather than trading one hardcoded color for another. The vendored CurrentLineBorder
            // property predates nullable annotations and is typed as a non-nullable Pen, but the
            // control genuinely accepts null at runtime (that is how you turn the border off).
            TextArea.TextView.CurrentLineBorder = null!;
        }

        private static Color Res(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b.Color;
            return fallback;
        }

        // ═══════════════════════════════════════════════════════════
        //  ZOOM
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Ctrl+wheel resizes the text, the way it does in every other editor.
        /// </summary>
        /// <remarks>
        /// It moves the APP-WIDE size and persists it, rather than being a private per-document
        /// zoom. Two knobs for one thing is how the gear's readout ends up lying about what you
        /// are looking at, and nobody wants the second tab they open to be a different size from
        /// the one they just adjusted.
        /// </remarks>
        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) { base.OnPreviewMouseWheel(e); return; }

            double next = EditorOptions.ClampFont(EditorOptions.FontSize + (e.Delta > 0 ? 1 : -1));
            e.Handled = true;
            if (next == EditorOptions.FontSize) return;      // already at an end of the range

            EditorOptions.FontSize = next;
            EditorOptions.Save();
            ZoomChanged?.Invoke();                           // the window fans it out (EditorBar.cs)
        }
    }
}
