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
            return FilesForCommand(_menuSeed)          // ResultsInteraction.cs
                .Where(p => ArchiveProvider.TrySplit(p, out _, out string e) && e.Length > 0)
                .ToList();
        }

        /// <summary>
        /// Whether a drop lands inside a WRITABLE archive, and where. A folder row under the
        /// pointer wins over the folder being browsed, the same rule DropTarget uses on disk:
        /// dropping onto a folder icon has to mean into THAT folder.
        /// </summary>
        private bool ArchiveDropTarget(FilePane pane, DependencyObject? src,
                                       out string archivePath, out string entryFolder)
        {
            if (!ArchiveTarget(pane, out archivePath, out entryFolder)) return false;
            if (!ArchiveWriter.CanWrite(archivePath)) return false;

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
            if (sources.Length == 0) return;

            var tab = _active;
            var policy = ArchiveCollision.Report;

            while (true)
            {
                string arc = archivePath, folder = entryFolder;
                var mode = policy;
                var progress = ArchiveProgress(tab);

                var r = await Task.Run(() => ArchiveWriter.Add(arc, folder, sources, mode,
                                                               progress, CancellationToken.None));

                if (r.Collisions.Count > 0 && policy == ArchiveCollision.Report)
                {
                    var dlg = new ConfirmDialog(
                        string.Format(Loc("Str_Dlg_ArchiveCollideMsg"), r.Collisions.Count.ToString("N0")),
                        string.Join(Environment.NewLine, r.Collisions.Take(8)),
                        Loc("Str_Btn_ArchiveAdd"),
                        Loc("Str_Chk_ArchiveReplace")) { Owner = this };
                    dlg.ShowDialog();

                    if (!dlg.Confirmed) { SetTabStatusKey(tab, "Str_Status_ArchiveCanceled"); return; }

                    policy = dlg.Check1Checked ? ArchiveCollision.Replace : ArchiveCollision.KeepBoth;
                    continue;
                }

                ReportArchive(tab, r, "Str_Status_ArchiveAdded", r.Changed.ToString("N0"));
                if (r.Ok) await RelistArchive(tab);
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
            var progress = ArchiveProgress(tab);
            var r = await Task.Run(() => ArchiveWriter.Delete(archivePath, entries,
                                                              progress, CancellationToken.None));

            ReportArchive(tab, r, "Str_Status_ArchiveDeleted", r.Changed.ToString("N0"));
            if (r.Ok) await RelistArchive(tab);
        }

        // ── Rename ───────────────────────────────────────────────
        /// <summary>Renames one entry. A folder takes everything under it with it.</summary>
        internal async Task ArchiveRename(string virtualPath)
        {
            if (!ArchiveProvider.TrySplit(virtualPath, out string archivePath, out string entry)
                || entry.Length == 0) return;

            if (!ArchiveWriter.CanWrite(archivePath))
            {
                SetTabStatusKey(_active, "Str_Status_ArchiveReadOnly");
                return;
            }

            int slash = entry.LastIndexOf('/');
            string leaf = slash < 0 ? entry : entry.Substring(slash + 1);

            var dlg = new RenameDialog(leaf) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            var tab = _active;
            string newName = dlg.NewName;
            var progress = ArchiveProgress(tab);
            var r = await Task.Run(() => ArchiveWriter.Rename(archivePath, entry, newName,
                                                               progress, CancellationToken.None));

            ReportArchive(tab, r, "Str_Status_ArchiveRenamed", newName);
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
        /// Folder rows are skipped rather than extracted. Rebuilding a whole subtree under temp
        /// is a different job from extracting one entry, and dragging a folder that silently
        /// produced nothing would be worse than saying so.
        /// </remarks>
        private string[] ExtractForDragOut(List<string> virtualPaths)
        {
            var extracted = new List<string>();
            bool skippedFolder = false;

            foreach (string v in virtualPaths)
            {
                if (!ArchiveProvider.TrySplit(v, out string archivePath, out string entry)
                    || entry.Length == 0) continue;

                if (_active.Results.FirstOrDefaultPath(v) is { IsDirectory: true })
                {
                    skippedFolder = true;
                    continue;
                }

                string? temp = ArchiveProvider.ExtractToTemp(archivePath, entry, out _);
                if (temp != null) extracted.Add(temp);
            }

            if (extracted.Count == 0 && skippedFolder)
                SetTabStatusKey(_active, "Str_Status_ArchiveNoDrag");

            return extracted.ToArray();
        }

        // ── Shared plumbing ──────────────────────────────────────
        /// <summary>
        /// A progress callback for the writer, throttled to one status update per whole percent.
        /// </summary>
        /// <remarks>
        /// The writer calls this once per entry, from a worker thread. A 50,000-entry archive
        /// would queue 50,000 dispatcher hops, and the reporting would then cost more than the
        /// rewrite - so the hop only happens when the number on screen would actually change.
        /// </remarks>
        private Action<int, int, string> ArchiveProgress(SearchTab tab)
        {
            int lastPercent = -1;
            return (done, total, _) =>
            {
                int percent = total > 0 ? (int)(done * 100L / total) : 0;
                if (percent == lastPercent) return;
                lastPercent = percent;

                Dispatcher.InvokeAsync(
                    () => SetTabStatusKey(tab, "Str_Status_ArchiveWriting", percent.ToString()),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
        }

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
                return;
            }

            if (r.Rejected.Count > 0)
                SetTabStatusKey(tab, "Str_Status_ArchiveSomeRejected", okArg,
                                r.Rejected.Count.ToString("N0"));
            else
                SetTabStatusKey(tab, okKey, okArg);
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
