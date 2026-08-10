using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using KillerShell.Models;

namespace KillerShell.Shell
{
    // Keeps a browsed folder live. Partial of MainWindow.
    //
    // Search results are a snapshot by nature and get away with going stale. A browsed folder
    // cannot: delete a file in another window and it has to disappear here too, or the listing is
    // lying about the disk.
    //
    // Only the ACTIVE tab is watched. Watching every background tab would mean a handle and a
    // buffer per tab for folders nobody is looking at; instead the watcher follows the active tab
    // and a tab being switched to gets a silent refresh on arrival, which covers anything that
    // changed while it was in the background.
    //
    // Events are debounced rather than applied as they arrive. A single file copy raises several
    // events, and copying a folder of ten thousand raises tens of thousands - applying each one
    // to a sorted, filtered collection view would be the quadratic-insert problem all over again.
    // Below a threshold the pending changes are applied incrementally; above it, relisting the
    // whole folder once is both cheaper and simpler to get right.
    public partial class MainWindow
    {
        private FileSystemWatcher? _watcher;
        private DispatcherTimer?   _watchDebounce;

        private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string OldPath, string NewPath)> _renamedPairs = [];
        private bool _watchOverflow;

        // Past this many distinct paths in one burst, relist instead of patching entry by entry.
        private const int RelistThreshold = 200;

        private void StartWatching(string folder)
        {
            StopWatching();

            try
            {
                _watcher = new FileSystemWatcher(folder)
                {
                    // Size and LastWrite so an in-place edit refreshes the columns, not just
                    // creates and deletes.
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };

                _watcher.Created += OnFsEvent;
                _watcher.Deleted += OnFsEvent;
                _watcher.Changed += OnFsEvent;
                _watcher.Renamed += OnFsRenamed;
                _watcher.Error   += OnFsError;

                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // No watch is possible on some paths - a CD, a locked-down share, a path that
                // vanished between listing and watching. The listing still stands; it just will
                // not update itself.
                StopWatching();
            }
        }

        private void StopWatching()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFsEvent;
                _watcher.Deleted -= OnFsEvent;
                _watcher.Changed -= OnFsEvent;
                _watcher.Renamed -= OnFsRenamed;
                _watcher.Error   -= OnFsError;
                _watcher.Dispose();
            }
            catch { /* already gone */ }
            _watcher = null;
        }

        // These arrive on a threadpool thread, so nothing here touches the collection directly.
        private void OnFsEvent(object sender, FileSystemEventArgs e) => QueueChange(e.FullPath);

        private void OnFsRenamed(object sender, RenamedEventArgs e)
        {
            string oldPath = e.OldFullPath, newPath = e.FullPath;
            Dispatcher.InvokeAsync(() =>
            {
                _renamedPairs.Add((oldPath, newPath));
                _touched.Add(oldPath);
                _touched.Add(newPath);

                _watchDebounce ??= CreateDebounceTimer();
                _watchDebounce.Stop();
                _watchDebounce.Start();
            }, DispatcherPriority.Background);
        }

        // The watcher's internal buffer overflowed, so an unknown number of events were dropped.
        // Nothing incremental can be trusted after that - relist.
        private void OnFsError(object sender, ErrorEventArgs e)
        {
            _watchOverflow = true;
            QueueChange(string.Empty);
        }

        private void QueueChange(string path)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (path.Length > 0) _touched.Add(path);

                _watchDebounce ??= CreateDebounceTimer();
                _watchDebounce.Stop();
                _watchDebounce.Start();
            }, DispatcherPriority.Background);
        }

        private DispatcherTimer CreateDebounceTimer()
        {
            // Long enough to swallow the several events a single save produces, short enough that
            // a change you made yourself feels immediate.
            var t = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (_, _) => { t.Stop(); FlushWatchChanges(); };
            return t;
        }

        private async void FlushWatchChanges()
        {
            var tab = _active;
            if (tab == null || !tab.IsBrowsing) { _touched.Clear(); _watchOverflow = false; return; }

            string folder = tab.CurrentFolder;

            // The folder we are standing in was itself deleted or renamed. Walk up to the nearest
            // parent that still exists rather than sitting on a listing of nothing.
            if (!Directory.Exists(folder))
            {
                _touched.Clear();
                _renamedPairs.Clear();
                _watchOverflow = false;
                string? up = folder;
                while (up != null && !Directory.Exists(up)) up = ParentOf(up);
                if (up != null) await NavigateTo(up);
                return;
            }

            bool relist = _watchOverflow || _touched.Count > RelistThreshold;
            var paths = _touched.ToList();
            var pairs = _renamedPairs.ToList();
            _touched.Clear();
            _renamedPairs.Clear();
            _watchOverflow = false;

            if (relist) { await NavigateTo(folder, record: false); return; }

            ApplyWatchChanges(tab, paths, pairs);
        }

        // One pass per changed path. Each is classified by what is on disk NOW rather than by
        // which event fired, because the events are already stale by the time the debounce
        // expires: a create followed by a delete should end as "not there", whichever order the
        // notifications happened to arrive in.
        private void ApplyWatchChanges(SearchTab tab, List<string> paths, List<(string OldPath, string NewPath)> pairs)
        {
            var byPath = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in tab.Results) byPath[r.FilePath] = r;

            int nextSeq = tab.Results.Count == 0 ? 0 : tab.Results.Max(r => r.Seq) + 1;
            bool changed = false;
            bool selectionRenamed = false;

            // Renames first, and IN PLACE: the generic create/delete loop below would otherwise
            // see the old path vanish and the new path appear and treat that as an unrelated
            // remove-then-add, which drops the row's object identity - exactly what selection and
            // the details-pane preview are keyed on (2026-08-03: renaming the selected file
            // showed a different, wrong thumbnail). Mutating the SAME object keeps the container,
            // the selection and the preview all pointed at the one row that actually changed.
            foreach (var (oldPath, newPath) in pairs)
            {
                if (!byPath.TryGetValue(oldPath, out var existing)) continue;

                string? parent = Path.GetDirectoryName(newPath);
                if (!string.Equals(parent, tab.CurrentFolder, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(newPath) && !Directory.Exists(newPath)) continue;

                bool wasSelected = ReferenceEquals(Pane.ResultsList?.SelectedItem, existing);

                existing.ApplyRename(newPath, Path.GetFileName(newPath));
                changed = true;
                if (wasSelected) selectionRenamed = true;

                paths.Remove(oldPath);
                paths.Remove(newPath);
                byPath.Remove(oldPath);
                byPath[newPath] = existing;
            }

            foreach (var path in paths)
            {
                // Only entries directly in this folder. A watcher is not recursive, but a rename
                // of the folder itself reports paths that are not children of it.
                string? parent = Path.GetDirectoryName(path);
                if (!string.Equals(parent, tab.CurrentFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isDir  = Directory.Exists(path);
                bool isFile = File.Exists(path);
                byPath.TryGetValue(path, out var existing);

                if (!isDir && !isFile)
                {
                    if (existing != null) { tab.Results.Remove(existing); changed = true; }
                    continue;
                }

                // Hidden entries are not listed, so one becoming hidden should leave too.
                if (IsHidden(path))
                {
                    if (existing != null) { tab.Results.Remove(existing); changed = true; }
                    continue;
                }

                if (existing == null)
                {
                    var entry = MakeEntry(path, tab.CurrentFolder, isDir, nextSeq++);
                    if (entry != null) { tab.Results.Add(entry); changed = true; }
                    continue;
                }

                // Already listed: refresh the columns that can move under us. Size and date are
                // plain properties with no change notification, so the row is replaced rather
                // than mutated - cheaper than making every column notifying for this one case.
                if (!isDir)
                {
                    var fresh = MakeEntry(path, tab.CurrentFolder, false, existing.Seq);
                    if (fresh != null && (fresh.SizeBytes != existing.SizeBytes ||
                                          fresh.Modified  != existing.Modified))
                    {
                        int at = tab.Results.IndexOf(existing);
                        if (at >= 0) { tab.Results[at] = fresh; changed = true; }
                    }
                }
            }

            if (changed)
                Pane.ResultsHeader.Text = string.Format(Loc("Str_Lbl_ResultsCount"), tab.Results.Count);

            // The renamed row's own object survived, so the name/path fields shown in the
            // details strip still read the OLD name until this refreshes them - the file's bytes
            // did not change, so the already-decoded preview image is left alone on purpose.
            if (selectionRenamed) UpdateDetailsPaneForSelection(Pane, animate: false);
        }

        private static bool IsHidden(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.Hidden) != 0; }
            catch { return false; }
        }

        // Anything that vanishes between being noticed and being stat'd just does not get added;
        // the next event will pick it up if it comes back.
        private static SearchResult? MakeEntry(string path, string folder, bool isDir, int seq)
        {
            try
            {
                if (isDir)
                {
                    var d = new DirectoryInfo(path);
                    return new SearchResult
                    {
                        FilePath    = d.FullName,
                        FileName    = d.Name,
                        Directory   = folder,
                        IsDirectory = true,
                        Modified    = d.LastWriteTime,
                        Seq         = seq,
                    };
                }

                var f = new FileInfo(path);
                return new SearchResult
                {
                    FilePath  = f.FullName,
                    FileName  = f.Name,
                    Directory = folder,
                    SizeBytes = f.Length,
                    Modified  = f.LastWriteTime,
                    Seq       = seq,
                };
            }
            catch { return null; }
        }
    }
}
