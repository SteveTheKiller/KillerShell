using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using KillerShell.Services;

namespace KillerShell.Shell
{
    // ═══════════════════════════════════════════════════════════
    //  FILE COMMANDS  -  what the keys and the menu actually call
    // ═══════════════════════════════════════════════════════════
    // The thin layer between a gesture and Services/FileOps: work out what to act on, where to
    // act, confirm if the action destroys something, hand it to FileOpDialog, then report.
    // Partial of MainWindow.
    //
    // Every one of these needs a TARGET FOLDER, and a search tab does not have one - its rows
    // come from all over the disk. So paste and new-folder are browse-only, and the ones that
    // act on the selection rather than on a folder (copy, cut, delete, rename) work in both.
    public partial class MainWindow
    {
        /// <summary>The folder a paste or a new folder would land in, or null on a search tab.</summary>
        private string? TargetFolder()
            => _active != null && _active.IsBrowsing && !string.IsNullOrEmpty(_active.CurrentFolder)
               && Directory.Exists(_active.CurrentFolder)
                   ? _active.CurrentFolder
                   : null;

        /// <summary>Selected rows, or the row under the pointer if nothing is selected.</summary>
        private List<string> SelectedPaths()
            => FilesForCommand(_menuSeed).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

        // ── Context-menu entry points ────────────────────────────
        // Thin wrappers rather than wiring the menu straight at the commands: the menu passes
        // (sender, args) and the commands take none, and a wrapper each keeps the XAML readable.
        internal void MenuCut_Click(object sender, RoutedEventArgs e)       => CutSelection();
        internal void MenuCopy_Click(object sender, RoutedEventArgs e)      => CopySelection();
        internal void MenuPaste_Click(object sender, RoutedEventArgs e)     => PasteIntoCurrentFolder();
        internal void MenuRename_Click(object sender, RoutedEventArgs e)    => RenameSelection();
        internal void MenuNewFolder_Click(object sender, RoutedEventArgs e) => NewFolderHere();

        /// <summary>
        /// Menu delete recycles. Shift-clicking the entry deletes permanently, the same modifier
        /// the key uses - so the two routes to Delete behave identically.
        /// </summary>
        internal void MenuDelete_Click(object sender, RoutedEventArgs e)
            => DeleteSelection(permanent: (System.Windows.Input.Keyboard.Modifiers
                                           & System.Windows.Input.ModifierKeys.Shift) != 0);

        // ── Clipboard ────────────────────────────────────────────

        /// <summary>
        /// Cut is Copy with a different Preferred DropEffect. Nothing moves until the paste - the
        /// same contract Explorer has, and the reason a cut you never paste is harmless.
        /// </summary>
        internal void CutSelection()      => PutFilesOnClipboard(DragDropEffects.Move);
        internal void CopySelection()     => PutFilesOnClipboard(DragDropEffects.Copy);

        private void PutFilesOnClipboard(DragDropEffects effect)
        {
            var files = SelectedPaths();
            if (files.Count == 0) return;

            try
            {
                var list = new System.Collections.Specialized.StringCollection();
                foreach (var f in files) list.Add(f);

                var data = new DataObject();
                data.SetFileDropList(list);

                // Without this blob the receiving app picks its own idea of copy versus move.
                var blob = new MemoryStream(BitConverter.GetBytes((int)effect));
                data.SetData("Preferred DropEffect", blob);

                Clipboard.SetDataObject(data, true);
                SetTabStatusKey(_active, "Str_Status_CopiedFiles", files.Count.ToString("N0"));
            }
            catch { SetTabStatusKey(_active, "Str_Status_ClipboardBusy"); }
        }

        /// <summary>
        /// Pastes whatever is on the clipboard into the browsed folder, honoring the drop effect
        /// the copying app asked for - so a cut from Explorer moves here, and a copy copies.
        /// </summary>
        internal void PasteIntoCurrentFolder()
        {
            string? target = TargetFolder();
            if (target == null) { SetTabStatusKey(_active, "Str_Status_PasteNeedsFolder"); return; }

            string[] files;
            bool move = false;

            try
            {
                if (!Clipboard.ContainsFileDropList()) return;

                var list = Clipboard.GetFileDropList();
                files = list.Cast<string>().Where(p => p != null).ToArray()!;
                if (files.Length == 0) return;

                // DROPEFFECT_MOVE is bit 1. Absent blob means copy, which is the safe reading.
                if (Clipboard.GetDataObject()?.GetData("Preferred DropEffect") is MemoryStream ms)
                {
                    var b = new byte[4];
                    ms.Position = 0;
                    if (ms.Read(b, 0, 4) == 4)
                        move = ((DragDropEffects)BitConverter.ToInt32(b, 0) & DragDropEffects.Move) != 0;
                }
            }
            catch { SetTabStatusKey(_active, "Str_Status_ClipboardBusy"); return; }

            RunCopyMove(files, target, move);
        }

        // ── Drop ─────────────────────────────────────────────────

        /// <summary>
        /// A real file drop onto a browsed folder. Explorer's modifier rules: Shift moves, Ctrl
        /// copies, and with neither it is a move within the same volume and a copy across one -
        /// which is what makes dragging to another drive feel safe and dragging within one feel
        /// like rearranging.
        /// </summary>
        internal void DropOntoFolder(string[] sources, string target, DragDropEffects allowed, bool ctrl, bool shift)
        {
            if (sources.Length == 0 || !Directory.Exists(target)) return;

            // Refuse to drop a folder into itself or into its own child - the walk would never
            // terminate and the user would watch it fill the disk.
            foreach (string s in sources)
                if (Directory.Exists(s) && IsInside(target, s))
                {
                    SetTabStatusKey(_active, "Str_Status_DropIntoSelf");
                    return;
                }

            bool move = shift || (!ctrl && (allowed & DragDropEffects.Move) != 0 && SameRoot(sources[0], target));
            RunCopyMove(sources, target, move);
        }

        private static bool IsInside(string candidate, string ancestor)
        {
            try
            {
                string a = Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar);
                string c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
                return c.Equals(a, StringComparison.OrdinalIgnoreCase)
                    || c.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool SameRoot(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)),
                                     Path.GetPathRoot(Path.GetFullPath(b)),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ── Delete ───────────────────────────────────────────────

        /// <summary>
        /// Delete recycles; Shift+Delete destroys. Only the destroying one asks first - a recycle
        /// is undoable from Explorer, so a prompt on every delete trains people to click through
        /// the prompt that actually matters.
        /// </summary>
        internal void DeleteSelection(bool permanent)
        {
            var paths = SelectedPaths();
            if (paths.Count == 0) return;

            if (permanent)
            {
                string msg = string.Format(Loc("Str_Dlg_DeleteMsg"), paths.Count.ToString("N0"));
                var dlg = new ConfirmDialog(msg, string.Join(Environment.NewLine, paths.Take(8)),
                                            Loc("Str_Btn_Delete")) { Owner = this };
                dlg.ShowDialog();
                if (!dlg.Confirmed) return;

                var r = FileOpDialog.Delete(this, paths);
                ReportResult(r);
            }
            else
            {
                // No progress dialog: the shell owns this one and shows its own if it is slow.
                var r = FileOps.Recycle(paths);

                // The shell's Access Denied box is suppressed now (FOF_NOERRORUI), so a refusal
                // arrives as a flag and is answered in our own dialog instead. Skipping the
                // refresh is deliberate: nothing has been deleted yet, and if the elevated retry
                // goes ahead the watcher picks the change up when that process finishes.
                if (r.AccessDenied && !IsElevated) { OfferElevatedRecycle(paths); return; }

                ReportResult(r);
            }

            RefreshAfterFileOp();
        }

        /// <summary>
        /// The shell refused the recycle on permissions. Windows used to draw its own Access
        /// Denied box here; this is the same offer in KillerShell's dialog. An elevated retry is
        /// genuinely the only thing that can still do it, so the offer is worth making rather
        /// than reporting a failure and stopping.
        /// </summary>
        private void OfferElevatedRecycle(List<string> paths)
        {
            var dlg = new ConfirmDialog(Loc("Str_Dlg_DeleteDeniedMsg"),
                                        string.Join(Environment.NewLine, paths.Take(8)),
                                        Loc("Str_Btn_RetryAdmin")) { Owner = this };
            dlg.ShowDialog();

            if (!dlg.Confirmed) { SetTabStatusKey(_active, "Str_Status_DeleteDenied"); return; }

            RecycleElevated(paths);   // Elevation.cs
        }

        // ── New folder / rename ──────────────────────────────────

        internal void NewFolderHere()
        {
            string? target = TargetFolder();
            if (target == null) { SetTabStatusKey(_active, "Str_Status_PasteNeedsFolder"); return; }

            string? made = FileOps.NewFolder(target, Loc("Str_Fo_NewFolderName"));
            if (made == null) { SetTabStatusKey(_active, "Str_Status_OpFailed"); return; }

            RefreshAfterFileOp();

            // Straight into a rename, the way Explorer does - a folder called "New folder" is
            // never what anyone wanted, it is just the fastest way to get to naming one.
            RenamePath(made);
        }

        internal void RenameSelection()
        {
            string? p = SelectedPaths().FirstOrDefault();
            if (p == null) return;
            RenamePath(p);
        }

        private void RenamePath(string path)
        {
            var dlg = new RenameDialog(Path.GetFileName(path)) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            string? err = FileOps.Rename(path, dlg.NewName);
            if (err != null) SetTabStatusKey(_active, "Str_Status_RenameFailed", err);
            else RefreshAfterFileOp();
        }

        // ── Shared plumbing ──────────────────────────────────────

        private void RunCopyMove(string[] sources, string target, bool move)
        {
            var r = FileOpDialog.CopyOrMove(this, sources, target, move);
            ReportResult(r);
            RefreshAfterFileOp();
        }

        private void ReportResult(FileOpResult r)
        {
            if (r.Failed.Count > 0)
                SetTabStatusKey(_active, "Str_Status_OpPartial",
                                r.Succeeded.ToString("N0"), r.Failed.Count.ToString("N0"));
            else if (r.Canceled)
                SetTabStatusKey(_active, "Str_Status_OpCanceled", r.Succeeded.ToString("N0"));
            else
                SetTabStatusKey(_active, "Str_Status_OpDone", r.Succeeded.ToString("N0"));
        }

        /// <summary>
        /// Bring the tree back in step after a file operation, and the listing too when nothing
        /// else is watching it. The tree has no watcher of its own, so it is always refreshed.
        /// </summary>
        private void RefreshAfterFileOp()
        {
            // Only when nothing else will do it. The watcher patches the active folder entry by
            // entry (BrowseWatcher.cs), which is the one way a delete can leave the scroll
            // position where it was; re-listing here as well cleared and refilled the collection
            // and threw the pane back to the top of the folder on every single file operation.
            // A null watcher means there is nothing to patch the list - This PC, demo mode, a
            // share that refused a watch - and there the re-list is still the only refresh.
            if (_watcher == null && _active != null && _active.IsBrowsing
                && !string.IsNullOrEmpty(_active.CurrentFolder))
                _ = NavigateTo(_active.CurrentFolder!, record: false);   // Browse.cs

            _ = RefreshTreeAsync();   // FolderTree.cs
        }
    }
}
