using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KillerShell.Models;
using KillerShell.Services;

namespace KillerShell.Shell
{
    // ═══════════════════════════════════════════════════════════
    //  WRITING INSIDE AN ARCHIVE  -  add, delete, rename, drag in
    // ═══════════════════════════════════════════════════════════
    // The UI half of Services/ArchiveWriter.cs, and only the UI half: work out what a gesture
    // means, ask when something would be overwritten, run the rewrite off the UI thread, report,
    // re-list. Every decision about what is safe to write lives in the writer, which is pure and
    // testable; nothing in here touches an archive directly. Partial of MainWindow.
    //
    // A rewrite is not a file copy. Adding one file to a zip rebuilds the whole zip, so the
    // status line carries a percentage rather than going quiet - on a multi-GB archive that is
    // the difference between "working" and "hung".
    //
    // THE ASYMMETRY, decided rather than stumbled into: things come IN as a real write and go
    // OUT only as a copy. A move out of an archive is an extract plus a delete-from-archive,
    // two steps with no way to make them one - a failure between them either loses the file or
    // silently leaves it in place, and neither is a thing a file manager may do to somebody's
    // data. Explorer treats zip drag-out as a copy for the same reason.
    public partial class MainWindow
    {
        private bool RefuseDemoArchiveWrite()
        {
            if (!DemoMode) return false;
            SetTabStatusKey(_active, "Str_Status_ArchiveCanceled");
            return true;
        }

        // ── Where a write would land ─────────────────────────────
        /// <summary>The archive and folder the focused tab is browsing inside, if any. The
        /// counterpart to TargetFolder() (FileCommands.cs), which is null here because nothing
        /// inside an archive passes Directory.Exists.</summary>
        private bool ArchiveTarget(out string archivePath, out string entryFolder)
            => ArchiveTarget(Pane, out archivePath, out entryFolder);

        private static bool ArchiveTarget(FilePane pane, out string archivePath, out string entryFolder)
        {
            archivePath = ""; entryFolder = "";
            var tab = pane.Active;
            return tab is { IsBrowsing: true }
                   && ArchiveProvider.TrySplit(tab.CurrentFolder, out archivePath, out entryFolder);
        }

        /// <summary>
        /// The entry paths a file command should act on, or null when the tab is not inside an
        /// archive at all. Separate from SelectedPaths() because that one filters on
        /// File.Exists / Directory.Exists, which is false for every row in here - so a command
        /// routed through it would find nothing to do and silently succeed at doing nothing.
        /// </summary>
        private List<string>? ArchiveSelection()
        {
            if (!ArchiveTarget(out _, out _)) return null;
            return [.. FilesForCommand(_menuSeed)          // ResultsInteraction.cs
                .Where(p => ArchiveProvider.TrySplit(p, out _, out string e) && e.Length > 0)];
        }

        /// <summary>
        /// Whether a drop lands inside a WRITABLE archive, and where. A folder row under the
        /// pointer wins over the folder being browsed, the same rule DropTarget uses on disk:
        /// dropping onto a folder icon has to mean into THAT folder.
        /// </summary>
        private static bool ArchiveDropTarget(FilePane pane, DependencyObject? src,
                                       out string archivePath, out string entryFolder)
        {
            if (!ArchiveTarget(pane, out archivePath, out entryFolder)) return false;
            if (DemoMode || !ArchiveWriter.CanWrite(archivePath)) return false;

            if (DataFor(ItemUnder(src)) is { IsDirectory: true } row
                && ArchiveProvider.TrySplit(row.FilePath, out string rowArchive, out string rowEntry)
                && rowEntry.Length > 0
                && string.Equals(rowArchive, archivePath, StringComparison.OrdinalIgnoreCase))
                entryFolder = rowEntry;

            return true;
        }

        // ── Add ──────────────────────────────────────────────────
        /// <summary>
        /// Copies files and folders from disk into an archive. The entry point for a drop into
        /// one and for a paste inside one.
        /// </summary>
        /// <remarks>
        /// The first pass runs with ArchiveCollision.Report, which writes NOTHING and hands the
        /// clashing names back. That is what makes "existing entries are never silently
        /// overwritten" a property of the code rather than a promise: the only way to overwrite
        /// is to come back through here a second time with Replace, which only the answered
        /// dialog does.
        /// </remarks>
        internal async Task ArchiveAdd(string[] sources, string archivePath, string entryFolder)
        {
            if (RefuseDemoArchiveWrite()) return;
            if (sources.Length == 0) return;

            var tab = _active;
            var policy = ArchiveCollision.Report;

            while (true)
            {
                string arc = archivePath, folder = entryFolder;
                var mode = policy;
                var r = FileOpDialog.RewriteArchive(this,
                    (progress, ct) => ArchiveWriter.Add(arc, folder, sources, mode, progress, ct));

                // The archive's own entries collide with each other: Add and Replace would only fail
                // the same way, so say what is wrong instead of asking how to resolve it.
                if (r.ExistingCollisions)
                {
                    r.ErrorKey = "Str_Status_ArchiveWriteFailed";
                    r.ErrorDetail = "it already holds names that Windows cannot write as separate files: "
                        + string.Join(", ", r.Collisions.Take(3));
                    ReportArchive(tab, r, "Str_Status_ArchiveAdded", "0");
                    return;
                }

                if (r.Collisions.Count > 0 && policy == ArchiveCollision.Report)
                {
                    var dlg = new ConfirmDialog(
                        string.Format(Loc("Str_Dlg_ArchiveCollideMsg"), r.Collisions.Count.ToString("N0")),
                        string.Join(Environment.NewLine, r.Collisions.Take(8)),
                        Loc("Str_Btn_ArchiveAdd"),
                        Loc("Str_Chk_ArchiveReplace")) { Owner = this };
                    dlg.ShowDialog();

                    if (!dlg.Confirmed)
                    {
                        SetTabStatusKey(tab, "Str_Status_ArchiveCanceled");
                        AppendDirectoryLinkStatus(tab, r.SkippedDirectoryLinks);
                        return;
                    }

                    policy = dlg.Check1Checked ? ArchiveCollision.Replace : ArchiveCollision.KeepBoth;
                    continue;
                }

                if (r.Ok) await RelistArchive(tab);
                ReportArchive(tab, r, "Str_Status_ArchiveAdded", r.Changed.ToString("N0"));
                return;
            }
        }

        // ── Delete ───────────────────────────────────────────────
        /// <summary>
        /// Removes entries from the archive the focused tab is inside. Always asks: there is no
        /// Recycle Bin inside an archive, so this is the permanent kind whichever key reached it,
        /// and the whole file is rewritten to do it.
        /// </summary>
        internal async Task ArchiveDelete(List<string> virtualPaths)
        {
            if (RefuseDemoArchiveWrite()) return;
            if (!ArchiveTarget(out string archivePath, out _)) return;

            if (!ArchiveWriter.CanWrite(archivePath))
            {
                SetTabStatusKey(_active, "Str_Status_ArchiveReadOnly");
                return;
            }

            var entries = new List<string>();
            foreach (string v in virtualPaths)
                if (ArchiveProvider.TrySplit(v, out _, out string e) && e.Length > 0) entries.Add(e);
            if (entries.Count == 0) return;

            var dlg = new ConfirmDialog(
                string.Format(Loc("Str_Dlg_ArchiveDeleteMsg"), entries.Count.ToString("N0")),
                string.Join(Environment.NewLine, entries.Take(8)),
                Loc("Str_Btn_Delete")) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            var tab = _active;
            var r = FileOpDialog.RewriteArchive(this,
                (progress, ct) => ArchiveWriter.Delete(archivePath, entries, progress, ct));

            ReportArchive(tab, r, "Str_Status_ArchiveDeleted", r.Changed.ToString("N0"));
            if (r.Ok) await RelistArchive(tab);
        }

        // ── Rename ───────────────────────────────────────────────
        /// <summary>Renames one entry. A folder takes everything under it with it.</summary>
        internal async Task ArchiveRename(string virtualPath)
        {
            if (RefuseDemoArchiveWrite()) return;
            if (!ArchiveProvider.TrySplit(virtualPath, out string archivePath, out string entry)
                || entry.Length == 0) return;

            if (!ArchiveWriter.CanWrite(archivePath))
            {
                SetTabStatusKey(_active, "Str_Status_ArchiveReadOnly");
                return;
            }

            int slash = entry.LastIndexOf('/');
            string leaf = slash < 0 ? entry : entry[(slash + 1)..];

            var dlg = new RenameDialog(leaf) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            var tab = _active;
            string newName = dlg.NewName;
            var r = FileOpDialog.RewriteArchive(this,
                (progress, ct) => ArchiveWriter.Rename(archivePath, entry, newName, progress, ct));

            ReportArchive(tab, r, "Str_Status_ArchiveRenamed", newName);
            if (r.Ok) await RelistArchive(tab);
        }

        /// <summary>Creates a real empty-folder entry in the ZIP currently being browsed.</summary>
        internal async Task ArchiveNewFolder()
        {
            if (RefuseDemoArchiveWrite()) return;
            if (!ArchiveTarget(out string archivePath, out string entryFolder)) return;
            if (!ArchiveWriter.CanWrite(archivePath))
            {
                SetTabStatusKey(_active, "Str_Status_ArchiveReadOnly");
                return;
            }

            var dlg = new RenameDialog(Loc("Str_Fo_NewFolderName")) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            var tab = _active;
            string name = dlg.NewName;
            var r = FileOpDialog.RewriteArchive(this,
                (progress, ct) => ArchiveWriter.CreateFolder(
                    archivePath, entryFolder, name, progress, ct));
            ReportArchive(tab, r, "Str_Status_ArchiveAdded", "1");
            if (r.Ok) await RelistArchive(tab);
        }

        // ── Dragging OUT: a copy, and only a copy ────────────────
        /// <summary>
        /// Extracts the dragged entries to temp copies and returns their real paths, so the
        /// ordinary CF_HDROP drag can carry them.
        /// </summary>
        /// <remarks>
        /// Synchronous on the UI thread on purpose: DoDragDrop is a modal loop that has to start
        /// from inside the mouse gesture that began it, so there is nowhere to await. Entries are
        /// normally small; a very large one will hold the window for the length of the inflate.
        ///
        /// Folder rows are recreated recursively under one private temp root. The drag still
        /// advertises Copy only: extracting a subtree and deleting it from the archive cannot be
        /// made atomic.
        /// </remarks>
        private string[] ExtractForDragOut(List<string> virtualPaths)
        {
            if (RefuseDemoArchiveWrite()) return [];
            var extracted = new List<string>();
            bool failed = false;

            foreach (string v in virtualPaths)
            {
                if (!ArchiveProvider.TrySplit(v, out string archivePath, out string entry)
                    || entry.Length == 0) continue;

                if (_active.Results.FirstOrDefaultPath(v) is { IsDirectory: true })
                {
                    string? folder = ArchiveProvider.ExtractFolderToTemp(archivePath, entry, out _);
                    if (folder != null) extracted.Add(folder); else failed = true;
                    continue;
                }

                string? temp = ArchiveProvider.ExtractToTemp(archivePath, entry, out _);
                if (temp != null) extracted.Add(temp);
            }

            if (extracted.Count == 0 && failed)
                SetTabStatusKey(_active, "Str_Status_ArchiveNoDrag");

            return [.. extracted];
        }

        // ── Compress and extract, from an ordinary folder ────────
        // The right-click verbs for archives that sit on disk. Both reuse the safe halves that
        // already exist: compressing is an add into a new, empty zip (so it inherits the writer's
        // temp-and-swap and its entry-name hygiene), and extracting walks the same
        // containment-checked path the folder drag-out uses. Neither ever overwrites anything:
        // a name that is already taken gets a numbered name, the way Explorer does it.

        /// <summary>Whether the menu's selection is all real archives this build can read,
        /// for the extract rows.</summary>
        private List<string> MenuArchives()
            => DemoMode ? [.. MenuFiles().Where(DemoArchives.Contains)]
                : [.. MenuFiles().Where(p => System.IO.File.Exists(p) && ArchiveProvider.IsReadable(p))];

        /// <summary>The selection as real files and folders on disk, for Compress. Empty
        /// inside an archive, where the rows are not on disk at all.</summary>
        private List<string> MenuDiskItems()
            => InArchive(Pane) ? [] : DemoMode ? [.. MenuFiles().Where(path => DemoFs.Find(path) != null)]
                : [.. MenuFiles().Where(Exists)];

        private static string ArchiveOutputName(string source, string extension)
        {
            string path = source.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            bool folder = DemoMode ? DemoFs.Has(path) : System.IO.Directory.Exists(path);
            string stem = folder ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
            return (stem.Length == 0 ? "Archive" : stem) + extension;
        }

        internal void MenuCompress_Click(object sender, RoutedEventArgs e)
        {
            var sources = MenuDiskItems();
            if (sources.Count == 0) return;

            string first = sources[0].TrimEnd(System.IO.Path.DirectorySeparatorChar);
            string? folder = System.IO.Path.GetDirectoryName(first);
            if (string.IsNullOrEmpty(folder)) return;   // a drive root has nowhere beside it

            string extension = ((sender as System.Windows.Controls.MenuItem)?.Tag as string) switch
            {
                "tar" => ".tar", "targz" => ".tar.gz", _ => ".zip",
            };
            string proposed = System.IO.Path.Combine(folder, ArchiveOutputName(first, extension));
            var tab = _active;
            if (DemoMode)
            {
                string? created = DemoArchives.Create(proposed, sources, path => DemoTextStore.Open(path, DemoTextFor(path)));
                if (created == null) SetTabStatusKey(tab, "Str_Status_ArchiveCanceled");
                else SetTabStatusKey(tab, "Str_Status_ArchiveCompressed", System.IO.Path.GetFileName(created));
                RefreshDemoArchiveListing(tab);
                return;
            }
            string zipPath = FreeName(proposed);
            if (extension != ".zip")
            {
                var created = FileOpDialog.RewriteArchive(this,
                    (progress, ct) => TarArchiveCreator.Create(zipPath, sources, extension == ".tar.gz", progress, ct));
                ReportArchive(tab, created, "Str_Status_ArchiveCompressed", System.IO.Path.GetFileName(zipPath));
                return;
            }

            // An empty zip first, so the writer has an archive to add into. It is removed again
            // if the add fails or is canceled, so a failure never leaves a stray empty zip.
            try
            {
                using var fs = new System.IO.FileStream(zipPath, System.IO.FileMode.CreateNew);
                using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
            }
            catch (Exception ex)
            {
                SetTabStatusKey(tab, "Str_Status_ArchiveWriteFailed", ex.Message);
                return;
            }

            var r = FileOpDialog.RewriteArchive(this,
                (progress, ct) => ArchiveWriter.Add(zipPath, "", sources, ArchiveCollision.KeepBoth, progress, ct));

            if (!r.Ok)
            {
                try { System.IO.File.Delete(zipPath); } catch { }
                ReportArchive(tab, r, "Str_Status_ArchiveCompressed", "");
                return;
            }
            ReportArchive(tab, r, "Str_Status_ArchiveCompressed", System.IO.Path.GetFileName(zipPath));
        }

        internal void MenuExtractHere_Click(object sender, RoutedEventArgs e) => ExtractArchives(intoOwnFolder: false);
        internal void MenuExtractTo_Click(object sender, RoutedEventArgs e)   => ExtractArchives(intoOwnFolder: true);

        /// <summary>
        /// Extracts each selected archive beside itself. "To folder" makes a new folder named
        /// after the archive. "Here" extracts into a hidden staging folder first and then moves
        /// each top-level item out, so a failure partway never leaves half an archive mixed in
        /// with the files already there, and an existing name is never overwritten.
        /// </summary>
        private void ExtractArchives(bool intoOwnFolder)
        {
            var archives = MenuArchives();
            if (archives.Count == 0) return;
            var tab = _active;
            string? landed = null;

            if (DemoMode)
            {
                foreach (string archive in archives)
                {
                    landed = DemoArchives.Extract(archive, intoOwnFolder);
                    if (landed == null) break;
                }
                if (landed == null) SetTabStatusKey(tab, "Str_Status_ArchiveCanceled");
                else SetTabStatusKey(tab, "Str_Status_ArchiveExtracted", landed);
                RefreshDemoArchiveListing(tab);
                return;
            }

            foreach (string archive in archives)
            {
                string? parent = System.IO.Path.GetDirectoryName(archive);
                if (string.IsNullOrEmpty(parent)) continue;

                string dest = intoOwnFolder
                    ? FreeName(System.IO.Path.Combine(parent, ArchiveStem(archive)), folder: true)
                    : System.IO.Path.Combine(parent, ".ksextract-" + Guid.NewGuid().ToString("N")[..8]);

                if (!intoOwnFolder)
                {
                    // Hidden so the listing does not flash a staging folder while it fills.
                    try
                    {
                        var stage = System.IO.Directory.CreateDirectory(dest);
                        stage.Attributes |= System.IO.FileAttributes.Hidden;
                    }
                    catch (Exception ex)
                    {
                        SetTabStatusKey(tab, "Str_Status_ArchiveExtractFailed", System.IO.Path.GetFileName(archive), ex.Message);
                        return;
                    }
                }

                var r = FileOpDialog.RewriteArchive(this, (_, ct) =>
                {
                    var res = new ArchiveWriteResult();
                    if (ArchiveProvider.ExtractAll(archive, dest, ct, out string? error))
                    {
                        ct.ThrowIfCancellationRequested();
                        res.Ok = true;
                    }
                    else { res.ErrorKey = "Str_Status_ArchiveExtractFailed"; res.ErrorDetail = error; }
                    return res;
                }, "Str_Status_ArchiveExtracting");

                if (r.Ok && !intoOwnFolder) r = MoveOutOfStage(dest, parent!);

                if (!r.Ok)
                {
                    // Whatever was half-written is removed: a partial extraction is worse than
                    // none, because it looks finished.
                    try { if (System.IO.Directory.Exists(dest)) System.IO.Directory.Delete(dest, recursive: true); } catch { }
                    SetTabStatusKey(tab, r.ErrorKey ?? "Str_Status_ArchiveExtractFailed",
                                    System.IO.Path.GetFileName(archive), r.ErrorDetail ?? string.Empty);
                    return;
                }

                landed = intoOwnFolder ? System.IO.Path.GetFileName(dest) : parent;
            }

            if (landed != null) SetTabStatusKey(tab, "Str_Status_ArchiveExtracted", landed);
        }

        private void RefreshDemoArchiveListing(SearchTab tab)
        {
            if (tab == _active && tab.IsBrowsing && !string.IsNullOrEmpty(tab.CurrentFolder))
                _ = NavigateTo(tab.CurrentFolder!, record: false);
        }

        /// <summary>Moves every top-level item out of a staging folder into
        /// <paramref name="parent"/>, renaming any that would collide, then removes the stage.</summary>
        private static ArchiveWriteResult MoveOutOfStage(string stage, string parent)
        {
            var res = new ArchiveWriteResult();
            try
            {
                foreach (string item in System.IO.Directory.GetFileSystemEntries(stage))
                {
                    bool isFolder = System.IO.Directory.Exists(item);
                    string target = FreeName(System.IO.Path.Combine(parent, System.IO.Path.GetFileName(item)), isFolder);
                    if (isFolder) System.IO.Directory.Move(item, target);
                    else System.IO.File.Move(item, target);
                }
                System.IO.Directory.Delete(stage, recursive: true);
                res.Ok = true;
            }
            catch (Exception ex)
            {
                res.ErrorKey = "Str_Status_ArchiveExtractFailed";
                res.ErrorDetail = ex.Message;
            }
            return res;
        }

        /// <summary>The archive's name without its archive extension, so "logs.tar.gz" extracts
        /// to "logs" rather than "logs.tar".</summary>
        private static string ArchiveStem(string archive)
        {
            string name = System.IO.Path.GetFileName(archive);
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return name[..^7];
            string stem = System.IO.Path.GetFileNameWithoutExtension(name);
            return stem.Length == 0 ? name : stem;
        }

        /// <summary>"report.zip" -> "report (2).zip" when the name is taken, numbering the way
        /// Explorer and FileOps do. A folder keeps its whole name, dots included.</summary>
        private static string FreeName(string path, bool folder = false)
        {
            if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) return path;
            string dir  = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
            string stem = folder ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
            string ext  = folder ? string.Empty : path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                ? ".tar.gz" : System.IO.Path.GetExtension(path);
            if (!folder && ext == ".tar.gz") stem = System.IO.Path.GetFileName(path)[..^7];
            int n = 2;
            string candidate;
            do { candidate = System.IO.Path.Combine(dir, stem + " (" + n++ + ")" + ext); }
            while (System.IO.File.Exists(candidate) || System.IO.Directory.Exists(candidate));
            return candidate;
        }

        // ── Shared plumbing ──────────────────────────────────────
        /// <summary>
        /// One place that turns a write result into a status line. Every refusal the writer can
        /// produce carries its own key, so a failure always says which failure it was - a write
        /// that appears to do nothing is the outcome this exists to make impossible.
        /// </summary>
        private void ReportArchive(SearchTab tab, ArchiveWriteResult r, string okKey, string okArg)
        {
            if (!r.Ok)
            {
                SetTabStatusKey(tab, r.ErrorKey ?? "Str_Status_ArchiveWriteFailed",
                                r.ErrorDetail ?? string.Empty);
                AppendDirectoryLinkStatus(tab, r.SkippedDirectoryLinks);
                return;
            }

            if (r.Rejected.Count > 0)
                SetTabStatusKey(tab, "Str_Status_ArchiveSomeRejected", okArg,
                                r.Rejected.Count.ToString("N0"));
            else
                SetTabStatusKey(tab, okKey, okArg);
            AppendDirectoryLinkStatus(tab, r.SkippedDirectoryLinks);
        }

        /// <summary>
        /// Re-read the archive after a write. Nothing watches an archive - there is no directory
        /// behind it and the watcher is deliberately stopped for one (Browse.cs) - so the
        /// listing only comes back by asking.
        /// </summary>
        private async Task RelistArchive(SearchTab tab)
        {
            if (tab != _active || string.IsNullOrEmpty(tab.CurrentFolder)) return;
            await NavigateTo(tab.CurrentFolder!, record: false);   // Browse.cs
        }
    }
}
