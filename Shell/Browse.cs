using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KillerShell.Models;

namespace KillerShell.Shell
{
    // Browsing a folder. Partial of MainWindow.
    //
    // A browsed listing goes into the SAME collection a search fills (tab.Results), as
    // SearchResult entries with IsDirectory set on the folders. That is the point of the whole
    // design rather than a convenience: it means the three views, the sort, the quick filter,
    // marquee selection, drag and drop and every context-menu command work on browsed entries
    // with no second implementation, and it leaves room for a search to drop its hits into the
    // folder you are already looking at instead of a separate list.
    //
    // Listing runs off the UI thread and lands in one assignment. A folder with 50k entries is
    // not rare (a node_modules, a mail store, a photo dump), and enumerating that on the
    // dispatcher would freeze the window the same way unbounded result batches used to.
    public partial class MainWindow
    {
        private CancellationTokenSource? _listCts;

        /// <summary>
        /// Show <paramref name="folder"/>. Records history unless this IS a history move, which
        /// is what stops Back from pushing the place you just came from and trapping you.
        /// </summary>
        private async Task NavigateTo(string folder, bool record = true)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;

            // This PC is a listing, not a directory, so it skips the path checks entirely.
            bool thisPc = IsThisPc(folder);

            // An archive location is the SAME kind of thing: a listing that no directory backs.
            // It rides the This PC path rather than growing a second one - no GetFullPath (the
            // a virtual path's separator is not a filename character), no Directory.Exists, no
            // watcher, no tree reveal. What must exist is the archive FILE.
            bool archive = Services.ArchiveProvider.TrySplit(folder, out string arcPath, out string arcEntry);
            if (archive && !File.Exists(arcPath))
            {
                SetTabStatusKey(_active, "Str_Status_BadPath", arcPath);
                return;
            }

            if (!thisPc && !archive)
            {
                try { folder = Path.GetFullPath(folder); }
                catch { SetTabStatusKey(_active, "Str_Status_BadPath", folder); return; }

                // Demo mode browses a machine that is not on disk (DemoFileSystem.cs), so the
                // existence check is what would stop every fabricated folder from opening -
                // including a double-click on one in the listing or a click on one in the tree.
                // GetFullPath above still runs: it only needs the path to be syntactically legal,
                // which every fabricated path is.
                if (!DemoMode && !Directory.Exists(folder))
                {
                    SetTabStatusKey(_active, "Str_Status_BadPath", folder);
                    return;
                }
            }

            var tab = _active;

            if (record && !string.Equals(tab.CurrentFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                // A new move truncates anything forward of here, the way every browser does it.
                if (tab.HistoryIndex < tab.History.Count - 1)
                    tab.History.RemoveRange(tab.HistoryIndex + 1, tab.History.Count - tab.HistoryIndex - 1);
                tab.History.Add(folder);
                tab.HistoryIndex = tab.History.Count - 1;
            }

            tab.CurrentFolder = folder;
            tab.IsBrowsing    = true;
            tab.Title         = thisPc  ? Loc("Str_Nav_ThisPc")
                              : archive ? ArchiveTitle(arcPath, arcEntry)
                              : FolderTitle(folder);

            // Cancel a listing still running for the folder we just left, or a slow network
            // share would land its results on top of the folder you moved to.
            _listCts?.Cancel();
            _listCts = new CancellationTokenSource();
            var ct = _listCts.Token;

            // The sentinel is never shown: the address row reads "This PC" the way Explorer's
            // does. It is what CaptureTab stores as the tab's search root too, which is not a
            // directory, so pressing Search here opens the folder picker instead of scanning.
            string shown = thisPc ? Loc("Str_Nav_ThisPc") : folder;
            Pane.RootPathBox.Text    = shown;
            Pane.ScopePathLabel.Text = shown;
            UpdateNavButtons();
            SetTabStatusKey(tab, "Str_Status_Listing", shown);

            List<SearchResult> entries;
            string? archiveError = null;
            if (thisPc) entries = ListDrives();
            else if (archive)
            {
                // Off the UI thread like an ordinary listing: reading a tar means walking the
                // whole file, and a big one on a slow disk would otherwise freeze the window.
                string capturedArc = arcPath, capturedEntry = arcEntry;
                try
                {
                    var listed = await Task.Run(() =>
                    {
                        var rows = Services.ArchiveProvider.List(capturedArc, capturedEntry, out string? err);
                        return (rows, err);
                    }, ct);
                    archiveError = listed.err;
                    entries = ArchiveRows(capturedArc, listed.rows);
                }
                catch (OperationCanceledException) { return; }
            }
            else
            {
                try { entries = await Task.Run(() => ListFolder(folder, ct), ct); }
                catch (OperationCanceledException) { return; }
            }

            if (ct.IsCancellationRequested || tab != _active) return;

            tab.Results.Clear();
            foreach (var e in entries) tab.Results.Add(e);

            ApplySort(tab);       // Results.cs - folders-first is added there while browsing
            ApplyFilter(tab);

            // Watch AFTER the listing lands, so the first events cannot arrive against a
            // collection that is still being filled (BrowseWatcher.cs). There is no directory
            // behind This PC to watch, so the previous folder's watcher is simply dropped.
            // Nothing to watch behind a fabricated folder either - and pointing the watcher at a
            // REAL folder while the listing shows invented rows is worse than not watching: the
            // first event has ApplyWatchChanges reconcile the list against the disk, which deletes
            // every fabricated row on screen (BrowseWatcher.cs).
            // Nothing to watch inside an archive either - the entries are not files on disk, and
            // pointing the watcher at the archive's own folder would have a save two folders
            // away reconcile the listing against the disk and delete every row on screen.
            if (thisPc || archive || DemoMode) StopWatching();
            else                               StartWatching(folder);

            Pane.ResultsHeader.Text = string.Format(Loc("Str_Lbl_ResultsCount"), tab.Results.Count);
            if (archiveError != null) SetTabStatusKey(tab, "Str_Status_ArchiveFailed", archiveError);
            else SetTabStatusKey(tab, "Str_Status_Listed", entries.Count.ToString("N0"));
            UpdateTabBar();

            UpdateFavoriteStar();   // Bookmarks.cs - a new folder changes what the star means
            UpdateLocationColumn();  // ViewOptions.cs - browsing needs no per-row folder
            UpdateRecentsButton();   // Recents.cs - the chevron is browse-only

            // Recorded AFTER the listing succeeded, not before: a path that turned out to be
            // unreadable is not somewhere you were, and putting it in the list would hand you a
            // row that fails every time you pick it.
            // Archive locations are deliberately NOT recorded: the recents menu drops any row
            // that fails Directory.Exists, so every one of them would be filtered out on the
            // next open anyway - a list entry that can never appear is worse than none.
            if (!archive) RecordRecent(folder);   // Recents.cs

            // Point the tree at where we landed, whichever route got us here - the tree's own
            // selection handler is what called this in the first place when it was the route,
            // and RevealInTree guards that case (FolderTree.cs). Not awaited: expanding the
            // chain can touch a slow drive and the listing is already on screen.
            // The tree is rooted AT the drives, so This PC is above everything it can show and
            // there is nothing to reveal.
            // The tree shows directories, and nothing inside an archive is one.
            if (!thisPc && !archive) _ = RevealInTree(folder);
        }

        // ── Archives as folders ──────────────────────────────────
        /// <summary>Tab title inside an archive: the entry folder if there is one, otherwise
        /// the archive's own file name, so a tab never reads as a bare "zip".</summary>
        private static string ArchiveTitle(string archivePath, string entryPath)
        {
            if (entryPath.Length == 0) return Path.GetFileName(archivePath);
            int slash = entryPath.LastIndexOf('/');
            return slash < 0 ? entryPath : entryPath.Substring(slash + 1);
        }

        /// <summary>
        /// Archive entries as listing rows. FilePath is the VIRTUAL path, so every existing
        /// row behavior keeps working unchanged - double-click routes back through
        /// ActivateEntry, the details pane and the icon lookup read the extension off the end
        /// of it, and sorting sees ordinary names and sizes.
        /// </summary>
        private static List<SearchResult> ArchiveRows(string archivePath, List<Services.ArchiveEntryInfo> entries)
        {
            var rows = new List<SearchResult>(entries.Count);
            int seq = 0;
            foreach (var e in entries)
            {
                rows.Add(new SearchResult
                {
                    FilePath    = Services.ArchiveProvider.Combine(archivePath, e.EntryPath),
                    FileName    = e.Name,
                    Directory   = archivePath,
                    IsDirectory = e.IsDirectory,
                    SizeBytes   = e.Size,
                    Modified    = e.Modified,
                    Seq         = seq++,
                });
            }
            return rows;
        }

        // ── This PC ──────────────────────────────────────────────
        // The top of the browse hierarchy: Up from a drive root lands here instead of stopping
        // dead, and it lists the drives the way Explorer's This PC does.
        //
        // It is a LISTING, not a folder - no path on disk contains C:\ and D:\ - so it travels
        // as a sentinel that cannot collide with a real path (':' is illegal in a Windows path
        // except after the drive letter). Everything downstream that needs a genuine directory
        // is gated either on IsThisPc or on Directory.Exists, which it already was: the search
        // root falls back to the folder picker, TargetFolder() returns null so paste and
        // new-folder stay disabled, and the watcher and tree reveal are skipped above. That is
        // what keeps This PC browse-only without a second code path for it.
        internal const string ThisPc = ":ThisPC:";

        internal static bool IsThisPc(string? path) => string.Equals(path, ThisPc, StringComparison.Ordinal);

        /// <summary>
        /// The drives, as browse entries. Not off the UI thread like ListFolder: this is a
        /// handful of rows, and DriveInfo.GetDrives does not touch the volumes themselves.
        /// </summary>
        private static List<SearchResult> ListDrives()
        {
            var list = new List<SearchResult>();
            int seq = 0;

            // The fabricated volumes, so This PC agrees with the tree's roots (DemoFileSystem.cs).
            if (DemoMode)
            {
                foreach (var root in DemoFs.Drives)
                    list.Add(new SearchResult
                    {
                        FilePath    = root,
                        FileName    = DemoFs.DriveLabel(root),
                        Directory   = string.Empty,
                        IsDirectory = true,
                        SizeBytes   = 0,
                        Modified    = default,
                        Seq         = seq++,
                    });
                return list;
            }

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (IOException) { return list; }
            catch (UnauthorizedAccessException) { return list; }

            foreach (var d in drives)
            {
                string root;
                try { root = d.RootDirectory.FullName; }
                catch { continue; }

                list.Add(new SearchResult
                {
                    FilePath    = root,
                    // Same "Local Disk (C:)" label the tree uses, from the one place that
                    // builds it (FolderTree.cs), so the two cannot drift.
                    FileName    = FolderNode.DriveLabel(d),
                    Directory   = string.Empty,
                    IsDirectory = true,
                    // Left at 0 like a folder. Free space would be the interesting number here,
                    // but the column says size, and a size column that means something else on
                    // one screen is worse than a blank one.
                    SizeBytes   = 0,
                    Modified    = default,
                    Seq         = seq++,
                });
            }

            return list;
        }

        // Everything in one pass, each entry stat'd once. Enumerating the FileSystemInfo rather
        // than the path string means Windows hands back size and timestamp with the entry, so the
        // sort keys cost nothing extra - the same trick worth doing in the search engine.
        private static List<SearchResult> ListFolder(string folder, CancellationToken ct)
        {
            var list = new List<SearchResult>();
            int seq = 0;

            // Demo mode lists the fabricated machine (DemoFileSystem.cs). Every field the real
            // pass below sets is set here too, and set the same way, because the sort keys, the
            // details columns and the icon view all read them - a row that skipped SizeBytes or
            // Seq would sort and draw differently from its neighbours for no visible reason.
            if (DemoMode)
            {
                foreach (var e in DemoFs.Children(folder))
                    list.Add(new SearchResult
                    {
                        FilePath    = Path.Combine(folder, e.Name),
                        FileName    = e.Name,
                        Directory   = folder,
                        IsDirectory = e.IsDir,
                        SizeBytes   = e.IsDir ? 0 : e.Size,
                        Modified    = e.Modified,
                        Seq         = seq++,
                    });
                return list;
            }

            try
            {
                // ONE interleaved pass, not directories-then-files.
                //
                // Enumerating them separately baked folders-first into the listing itself, which
                // left the folders-on-top toggle with nothing to do: under the default "as found"
                // sort the view carries no SortDescription at all, so switching the option off
                // just fell back to the underlying collection order - which was already grouped.
                // The toggle looked dead because the grouping was never the sort's doing.
                //
                // Discovery order is genuinely mixed now (NTFS hands these back alphabetically),
                // so "as found" means what it says and folders-first is purely a view concern.
                foreach (var e in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    if (ct.IsCancellationRequested) return list;

                    // One Attributes read, reused: each call is a stat on some providers.
                    FileAttributes a;
                    try { a = e.Attributes; } catch { continue; }   // vanished mid-enumeration

                    if (!ShowHidden && (a & FileAttributes.Hidden) != 0) continue;   // ViewOptions.cs

                    bool isDir = (a & FileAttributes.Directory) != 0;
                    list.Add(new SearchResult
                    {
                        FilePath    = e.FullName,
                        FileName    = e.Name,
                        Directory   = folder,
                        IsDirectory = isDir,
                        SizeBytes   = isDir ? 0 : SafeLength((FileInfo)e),
                        Modified    = SafeWriteTime(e),
                        Seq         = seq++,
                    });
                }
            }
            catch (UnauthorizedAccessException) { /* listed what we could see */ }
            catch (IOException) { }

            return list;
        }

        // A file can vanish or refuse a stat between being enumerated and being read. Neither is
        // worth losing the whole listing over.
        private static DateTime SafeWriteTime(FileSystemInfo i)
        {
            try { return i.LastWriteTime; } catch { return default; }
        }

        private static long SafeLength(FileInfo f)
        {
            try { return f.Length; } catch { return 0; }
        }

        private static string FolderTitle(string folder)
        {
            string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            return name.Length > 0 ? name : folder;   // a drive root has no name component
        }

        // ── History ──────────────────────────────────────────────
        internal async void NavBack_Click(object sender, RoutedEventArgs e)
        {
            var t = _active;
            if (t.HistoryIndex <= 0) return;
            t.HistoryIndex--;
            await NavigateTo(t.History[t.HistoryIndex], record: false);
        }

        internal async void NavForward_Click(object sender, RoutedEventArgs e)
        {
            var t = _active;
            if (t.HistoryIndex >= t.History.Count - 1) return;
            t.HistoryIndex++;
            await NavigateTo(t.History[t.HistoryIndex], record: false);
        }

        internal async void NavUp_Click(object sender, RoutedEventArgs e)
        {
            string? parent = ParentOf(_active.CurrentFolder);
            if (parent != null) await NavigateTo(parent);
        }

        // Null only at the very top: a drive root's parent is This PC, and This PC has none.
        //
        // The path goes to GetParent AS IS. Trimming the trailing separator first turned "C:\"
        // into "C:", which Windows reads as a DRIVE-RELATIVE path and resolves against whatever
        // that drive's current directory happens to be - so Up from C:\ jumped to a folder near
        // the process's working directory instead of going up. Untrimmed, GetParent("C:\")
        // returns null, which is exactly the drive-root signal wanted here. Nothing else can
        // arrive with a trailing separator: NavigateTo runs every path through GetFullPath,
        // which only leaves one on a drive root.
        private static string? ParentOf(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            if (IsThisPc(folder)) return null;

            // Inside an archive, Up walks the entry path first and then steps OUT of the
            // archive into the folder holding it, so climbing never dead-ends at the archive
            // root (Services/ArchiveProvider.cs).
            if (Services.ArchiveProvider.TrySplit(folder, out string arc, out string entry))
            {
                if (entry.Length > 0) return Services.ArchiveProvider.Parent(folder);
                folder = arc;   // at the archive root: up means the archive FILE's own folder
            }

            try
            {
                var parent = System.IO.Directory.GetParent(folder);
                return parent?.FullName ?? ThisPc;
            }
            catch { return null; }
        }

        private void UpdateNavButtons()
        {
            var t = _active;
            Pane.NavBackBtn.IsEnabled    = t.HistoryIndex > 0;
            Pane.NavForwardBtn.IsEnabled = t.HistoryIndex < t.History.Count - 1;
            Pane.NavUpBtn.IsEnabled      = ParentOf(t.CurrentFolder) != null;
        }

        /// <summary>Enter a folder, or open a file. What a double-click means in browse mode.</summary>
        internal async void ActivateEntry(SearchResult r)
        {
            if (r.IsDirectory) { await NavigateTo(r.FilePath); return; }

            // A zip or a tarball is a place, not a document: entering it is what a file browser
            // is for, and launching it would hand the job to whatever else is installed. Only
            // formats this build can actually read enter - a .rar still launches, because
            // WinRAR opening it is more useful than an error (Services/ArchiveProvider.cs).
            if (Services.ArchiveProvider.IsReadable(r.FilePath) && File.Exists(r.FilePath))
            {
                await NavigateTo(r.FilePath);
                return;
            }

            // Inside an archive there is nothing on disk to launch, so the entry is extracted
            // to a temp copy first and THAT is opened. The copy is deliberately a copy: edits
            // to it do not travel back into the archive, which is honest about what read-only
            // browsing can promise.
            if (Services.ArchiveProvider.TrySplit(r.FilePath, out string arc, out string entry) && entry.Length > 0)
            {
                SetTabStatusKey(_active, "Str_Status_Extracting", r.FileName);
                var tab = _active;

                // A block lambda returning the tuple, NOT an expression one: the out-parameter
                // has to be captured to report WHY an extraction failed, and an expression
                // lambda mixing a pattern with a null branch leaves Task.Run's return type to
                // inference, which is exactly where a nullability warning would come from.
                var (temp, error) = await Task.Run(() =>
                {
                    string? p = Services.ArchiveProvider.ExtractToTemp(arc, entry, out string? err);
                    return (Path: p, Error: err);
                });

                if (temp == null)
                {
                    SetTabStatusKey(tab, "Str_Status_ExtractFailed", error ?? r.FileName);
                    return;
                }
                SetTabStatus(tab, string.Empty);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(temp) { UseShellExecute = true });
                return;
            }

            if (File.Exists(r.FilePath))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(r.FilePath) { UseShellExecute = true });
        }
    }
}
