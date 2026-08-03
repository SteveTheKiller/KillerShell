using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using KillerShell.Models;

// Editor tabs: opening a file for editing, and where it lands. Partial of MainWindow.
//
// ONE SEAM, deliberately. Everything that wants a file edited - the prompt script, the
// PowerShell profile, the results menu - goes through OpenForEditing, so there is a single
// method that decides what "edit" means and no call site holds an opinion about it.
//
// The placement rule is the same one the shell now follows (TerminalTabs.cs): a new tab in the
// FOCUSED pane, every time. Nothing here gets to be clever about which pane you meant. Asking
// twice for the same file opens nothing new: the tab already holding it comes forward, because
// two editors over one file is a way to lose work rather than a feature.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // A BOM on a NEW file, for the same reason PromptScript.cs writes one: PowerShell 5.1
        // reads a BOM-less file as the system ANSI codepage, so every box-drawing glyph in a
        // script written without one comes back as mojibake. Only these extensions, because
        // everywhere else a BOM is noise other tools have to step over. An EXISTING file keeps
        // whatever it already had - nothing here ever adds one (EditorControl.Detect).
        private static readonly string[] BomExtensions = { ".ps1", ".psm1", ".psd1" };

        // E70F, the pencil, so a document tab is not mistaken for a folder or a shell.
        private static readonly string GlyphEdit = ((char)0xE70F).ToString();

        /// <summary>
        /// Biggest file the editor will open.
        /// </summary>
        /// <remarks>
        /// AvalonEdit holds the whole document in memory and builds five balanced trees over it,
        /// so a log the size of a DVD does not open slowly - it locks the window while it tries.
        /// An Edit row that refuses out loud beats one that freezes the app, and 32 MB is far
        /// past anything anybody edits by hand.
        /// </remarks>
        private const long MaxEditBytes = 32L * 1024 * 1024;

        // ═══════════════════════════════════════════════════════════
        //  OPEN
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Open <paramref name="path"/> in an editor tab, creating the file if it is not there.
        /// </summary>
        /// <remarks>
        /// Created rather than refused, because the file that turns out to be missing is usually
        /// $PROFILE: on a machine nobody has customized PowerShell on it does not exist at all,
        /// and an edit row that does nothing on a fresh machine has failed at the one moment it
        /// was wanted.
        /// </remarks>
        internal void OpenForEditing(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // --demo: the results describe a machine that does not exist, so none of the disk
            // work below can run - and the create-if-missing step would be actively wrong, since
            // it would write empty files into the REAL folders the fabricated paths name.
            // DemoTextFor invents a body to match the extension (DemoMode.cs).
            if (DemoMode)
            {
                foreach (var pane in LivePanes())
                    foreach (var open in pane.Tabs)
                        if (open.Editor != null &&
                            string.Equals(open.Editor.FilePath, path, StringComparison.OrdinalIgnoreCase))
                        { FocusPane(pane); SwitchToTab(open); return; }

                CaptureTab(_active);
                var demoTab = CreateEditorTab(path, DemoTextFor(path));
                if (demoTab != null) ActivateTab(demoTab);
                return;
            }

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

                if (!File.Exists(path))
                {
                    bool bom = Array.IndexOf(BomExtensions,
                                             Path.GetExtension(path).ToLowerInvariant()) >= 0;
                    File.WriteAllText(path, string.Empty, new UTF8Encoding(bom));
                }

                long size = new FileInfo(path).Length;
                if (size > MaxEditBytes)
                {
                    SetTabStatusKey(_active, "Str_Ed_TooBig", Path.GetFileName(path),
                                    (size / 1024d / 1024d).ToString("N0"));
                    return;
                }
            }
            catch (Exception ex)
            {
                SetTabStatusKey(_active, "Str_Ed_OpenFailed", Path.GetFileName(path), ex.Message);
                return;
            }

            // Already open? Go there. Two views over one file is how an edit gets overwritten by
            // the other copy's save, and nothing about the second tab would say so.
            foreach (var pane in LivePanes())            // Panes.cs
                foreach (var open in pane.Tabs)
                    if (open.Editor != null &&
                        string.Equals(open.Editor.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        FocusPane(pane);                 // Panes.cs
                        SwitchToTab(open);               // Tabs.cs
                        return;
                    }

            CaptureTab(_active);                         // Tabs.cs - the outgoing tab keeps its state
            var tab = CreateEditorTab(path);
            if (tab != null) ActivateTab(tab);
        }

        /// <summary>
        /// A blank document in a new tab in the focused pane. The rail's pencil, and the only
        /// way into the editor that does not start from a file.
        /// </summary>
        /// <remarks>
        /// Nothing is written anywhere until you save it, and the first save is the one that
        /// asks where (SaveActiveEditor) - which is the whole reason FilePath is allowed to be
        /// empty. Deliberately NOT reused when one is already open: two scratch buffers is a
        /// normal thing to want, and unlike a file there is no risk of one save clobbering the
        /// other, because neither of them points anywhere yet.
        /// </remarks>
        internal void NewDocument()
        {
            CaptureTab(_active);
            var tab = CreateEditorTab(string.Empty);
            if (tab != null) ActivateTab(tab);
        }

        internal void NewDoc_Click(object sender, RoutedEventArgs e) => NewDocument();

        /// <summary>
        /// Ctrl+F7: the same blank document as plain F7 (NewDocument), except this tab's save
        /// retries elevated on an access-denied write instead of just failing (SaveActiveEditor /
        /// Elevation.cs RetrySaveElevated). Only THIS tab gets that behavior - a document opened
        /// the normal way keeps failing normally on a permissions error, so elevation is never a
        /// surprise.
        /// </summary>
        internal void NewDocumentAdmin()
        {
            CaptureTab(_active);
            var tab = CreateEditorTab(string.Empty);
            if (tab == null) return;
            tab.Editor!.ElevatedSaveOnFail = true;
            ActivateTab(tab);
        }

        internal void NewDocAdmin_Click(object sender, RoutedEventArgs e) => NewDocumentAdmin();

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        /// <param name="demoText">
        /// --demo only. When set, the document is filled from this string instead of read off
        /// disk, so a screenshot can show a script open with its highlighting without that
        /// script existing on the machine taking the picture. Every other part of the tab is
        /// built exactly as a real one, which is the point: what is captured is the real editor.
        /// </param>
        private SearchTab? CreateEditorTab(string path, string? demoText = null)
        {
            // Read off the OUTGOING tab, before CreateTab moves _active: an untitled document
            // has no folder of its own, and the folder you were standing in is where the Save As
            // prompt should open.
            string folder = path.Length == 0 ? _active.CurrentFolder
                                             : Path.GetDirectoryName(path) ?? string.Empty;

            // Loaded BEFORE the tab is registered, so a file that cannot be read leaves no
            // half-built tab behind to close.
            var editor = new Editing.EditorControl(path);
            if (path.Length == 0) editor.LoadEmpty();    // untitled - there is nothing to read
            else if (demoText != null) editor.LoadDemo(demoText);
            else if (!editor.LoadFile(out string error))
            {
                SetTabStatusKey(_active, "Str_Ed_OpenFailed", Path.GetFileName(path), error);
                return null;
            }

            var tab = CreateTab();                       // Tabs.cs - registers it in this pane
            tab.Editor     = editor;
            tab.TabGlyph   = GlyphEdit;
            tab.IsBrowsing = false;

            // The address row reads the file's FOLDER rather than "no folder selected", and the
            // nav buttons stay meaningful, exactly as they do on a shell tab.
            tab.CurrentFolder = folder;
            tab.RootPath      = folder;

            SetEditorTitle(tab);
            editor.DirtyChanged += () => { SetEditorTitle(tab); SyncEditorBar(tab); };

            // Ctrl+wheel moves the app-wide size, so the other open documents and both bars have
            // to follow it (Editing/EditorControl.OnPreviewMouseWheel).
            editor.ZoomChanged += ApplyEditorOptions;

            // Menu rows the control cannot carry out itself, because they are about the tab, the
            // bar or the settings rather than about the text (Editing/EditorMenu.cs).
            editor.MenuCommand += cmd =>
            {
                switch (cmd)
                {
                    case Editing.EditorMenuCommand.GoToLine:
                        EditorGoto_Click(this, new RoutedEventArgs());     // EditorBar.cs
                        break;
                    case Editing.EditorMenuCommand.ToggleWrap:
                        EditorWrap_Click(this, new RoutedEventArgs());
                        break;
                    case Editing.EditorMenuCommand.Save:
                        SaveActiveEditor();
                        break;
                    case Editing.EditorMenuCommand.Settings:
                        EditorGear_Click(this, new RoutedEventArgs());
                        break;
                    case Editing.EditorMenuCommand.CloseTab:
                        CloseTab(tab);                                     // Tabs.cs
                        break;
                }
            };
            return tab;
        }

        /// <summary>The file name, with a dot in front while there are unsaved changes.</summary>
        /// <remarks>
        /// A dot rather than the usual asterisk. The tab already carries a glyph on its left and
        /// a close x on its right, and at this size an asterisk reads as part of the file name
        /// instead of as a mark on it.
        /// </remarks>
        private void SetEditorTitle(SearchTab t)
        {
            if (t.Editor == null) return;
            string name = t.Editor.IsUntitled ? Loc("Str_Ed_Untitled")
                                              : Path.GetFileName(t.Editor.FilePath);
            t.Title = t.Editor.Dirty ? ((char)0x2022) + " " + name : name;
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIVATION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Swap the pane between its listing and a document. Called from ActivateTab, so it runs
        /// on every tab switch in either pane.
        /// </summary>
        /// <remarks>
        /// Runs AFTER ApplyTerminalView and quietly re-makes two of its decisions. That is
        /// deliberate: the terminal path has a live pty on the other end of it, and leaving it
        /// to reach its own conclusions untouched is worth one redundant assignment here.
        /// </remarks>
        private void ApplyEditorView(SearchTab t)
        {
            bool editing = t.Editor != null;

            Pane.EditorHost.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;

            // MOVED rather than rebuilt, for the same reason the terminal is: the control holds
            // the document, the undo stack and the caret, and a fresh one per activation would
            // throw away all three on every tab switch.
            Pane.EditorSlot.Content = editing ? t.Editor : null;
            if (!editing) return;

            Pane.ResultsList.Visibility = Visibility.Collapsed;
            ApplyPaneToolbarMode(true);   // TerminalTabs.cs - sorting a document means nothing
            SyncEditorBar(t);             // EditorBar.cs - the strip belongs to the pane, so it
                                          // has to be repointed at the incoming document

            var editor = t.Editor!;
            // Focus has to wait for the swap to lay out, or it lands on an element that is still
            // collapsed and silently does nothing.
            Dispatcher.BeginInvoke(new Action(() => editor.TextArea.Focus()),
                                   System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>Tear a document down when its tab closes. Called from FinishCloseTab.</summary>
        private void CloseEditor(SearchTab t)
        {
            if (t.Editor == null) return;
            if (ReferenceEquals(Pane.EditorSlot.Content, t.Editor)) Pane.EditorSlot.Content = null;
            t.Editor = null;
        }

        // ═══════════════════════════════════════════════════════════
        //  SAVE
        // ═══════════════════════════════════════════════════════════
        /// <summary>Ctrl+S. A no-op on any tab that is not a document.</summary>
        private void SaveActiveEditor()
        {
            var t = _active;
            if (t.Editor == null) return;

            // An untitled document has nowhere to be written yet, so the first save asks. Backing
            // out of the dialog cancels the save outright rather than picking somewhere for you.
            if (t.Editor.IsUntitled)
            {
                if (!PromptSaveAs(t.Editor)) return;

                // It has a folder now, so the tab's address row and nav buttons get one too.
                t.CurrentFolder = Path.GetDirectoryName(t.Editor.FilePath) ?? string.Empty;
                t.RootPath      = t.CurrentFolder;
            }

            string name = Path.GetFileName(t.Editor.FilePath);
            if (t.Editor.SaveFile(out string error, out bool accessDenied))
            {
                SetTabStatusKey(t, "Str_Ed_Saved", name);
            }
            else if (accessDenied && t.Editor.ElevatedSaveOnFail)
            {
                // Ctrl+F7's whole point: a permission-denied write on THIS tab retries through
                // a second, elevated instance instead of just failing (Elevation.cs
                // RetrySaveElevated). Title/bar refresh happens once that retry resolves.
                RetrySaveElevated(t, name);
                return;
            }
            else
            {
                SetTabStatusKey(t, "Str_Ed_SaveFailed", name, error);
            }

            SetEditorTitle(t);
            SyncEditorBar(t);   // EditorBar.cs - the save button drops out of the accent
        }

        /// <summary>
        /// Ask where an untitled document should go, and hand the answer to the editor. False
        /// if the user backed out.
        /// </summary>
        /// <remarks>
        /// The stock shell dialog rather than the app's own FolderPickerDialog: that one picks a
        /// FOLDER, and this needs a name typed into it as well. It is also the one surface here
        /// that has to look like every other Save As on the machine.
        /// </remarks>
        private bool PromptSaveAs(Editing.EditorControl editor)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title           = Loc("Str_Ed_SaveAs"),
                Filter          = Loc("Str_Ed_AllFiles") + "|*.*",
                OverwritePrompt = true,

                // No silent extension. AddExtension with an all-files filter appends nothing
                // anyway, and a shell script typed as "deploy" should stay "deploy".
                AddExtension    = false,
            };

            // Only when it still exists: a tab restored from a session can name a folder that
            // has since gone, and the dialog answers a dead InitialDirectory by opening
            // somewhere of its own choosing with no explanation.
            string folder = _active.CurrentFolder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dlg.InitialDirectory = folder;

            if (dlg.ShowDialog(this) != true) return false;

            // The same BOM rule OpenForEditing uses on a file it creates: PowerShell 5.1 reads a
            // BOM-less script as the system ANSI codepage, so a .ps1 written without one comes
            // back as mojibake the first time it prints a box-drawing glyph.
            bool bom = Array.IndexOf(BomExtensions,
                                     Path.GetExtension(dlg.FileName).ToLowerInvariant()) >= 0;
            editor.AdoptPath(dlg.FileName, new UTF8Encoding(bom));
            return true;
        }

        /// <summary>Ask before throwing away unsaved changes. True to go ahead with the close.</summary>
        /// <remarks>
        /// The only modal question in the tab lifecycle, and it earns one: every other close
        /// throws away a search that can be re-run in a second, while this one throws away
        /// typing that exists nowhere else.
        /// </remarks>
        private bool ConfirmDiscard(SearchTab t)
        {
            if (t.Editor == null || !t.Editor.Dirty) return true;

            // The detail line is the path, which an untitled document does not have - and a
            // blank line under the question reads as a rendering fault rather than as "no file".
            var dlg = new ConfirmDialog(Loc("Str_Dlg_DiscardMsg"),
                                        t.Editor.IsUntitled ? Loc("Str_Ed_Untitled") : t.Editor.FilePath,
                                        Loc("Str_Btn_Discard")) { Owner = this };
            dlg.ShowDialog();
            return dlg.Confirmed;
        }

        /// <summary>Re-color every open document after a theme or accent switch.</summary>
        private void RefreshEditorThemes()
        {
            foreach (var p in new[] { LeftPane, RightPane })
                foreach (var t in p.Tabs)
                    t.Editor?.ApplyTheme();
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD OWNERSHIP
        // ═══════════════════════════════════════════════════════════
        /// <summary>True while the caret is inside a document.</summary>
        /// <remarks>
        /// Walked up the tree rather than tested against one type, because the editor's find bar
        /// is a child of it: with a straight "is TextArea" test, typing in that bar would fall
        /// back to the window's own bindings and the first Backspace would navigate a folder.
        /// </remarks>
        internal bool EditorHasFocus
        {
            get
            {
                var d = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
                while (d != null)
                {
                    if (d is Editing.EditorControl) return true;
                    d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                      ? VisualTreeHelper.GetParent(d)
                      : LogicalTreeHelper.GetParent(d);
                }
                return false;
            }
        }

        /// <summary>
        /// Chords that belong to the WINDOW even while a document has focus. Everything not
        /// listed here reaches the editor.
        /// </summary>
        /// <remarks>
        /// The shell's list plus Ctrl+S and Ctrl+G. It reuses the shell's list because the two
        /// surfaces want exactly the same thing from the window - tabs, panes and overlays,
        /// nothing that touches text - and one list means a chord added for one cannot quietly
        /// go missing in the other. Those two are NOT in the shared list on purpose: over a pty
        /// Ctrl+S is XOFF, which would freeze the terminal with no obvious way back, and Ctrl+G
        /// is a bell the shell may well want to ring.
        /// </remarks>
        private bool IsEditorChord(System.Windows.Input.KeyEventArgs e, bool ctrl, bool shift, bool alt)
        {
            if (ctrl && !shift && !alt
                && (e.Key == System.Windows.Input.Key.S || e.Key == System.Windows.Input.Key.G))
                return true;

            return IsWindowChord(e, ctrl, shift, alt);
        }
    }
}
